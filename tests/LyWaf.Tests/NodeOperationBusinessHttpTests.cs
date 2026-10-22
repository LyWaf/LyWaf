using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeOperationBusinessHttpTests
{
    [Fact]
    public async Task ConfigurationLifecycleAndReadOperationsPreserveBusinessFieldsOverHttp()
    {
        var oldPath = SharedData.ConfigFilePath;
        IConfigurationRoot? root = null;
        try
        {
            await using var host = await NodeContractHttpHost.Start(configure: (services, h) =>
            {
                SharedData.ConfigFilePath = Path.Combine(h.Directory, "config.yaml");
                File.WriteAllText(SharedData.ConfigFilePath, "SpeedLimit:\n  Value: old\n");
                root = new ConfigurationBuilder().AddYamlFile(SharedData.ConfigFilePath, false, false).Build();
                services.AddSingleton<IConfiguration>(root);
                services.AddSingleton<IOptionsMonitor<DistributionOptions>>(new StaticOptionsMonitor<DistributionOptions>(new()
                    { NodeId = "receiver", StateDirectory = Path.Combine(h.Directory, "distribution") }));
                services.AddSingleton<ConfigVersionService>(_ => new(new HttpContextAccessor()));
                services.AddSingleton<ConfigurationLifecycleService>();
                services.AddSingleton<DistributionNodeService>();
                services.AddSingleton<NodeCommandInbox>();
                services.AddSingleton<NodeIdentityService>();
                services.AddHostedService(sp => sp.GetRequiredService<NodeIdentityService>());
                services.AddSingleton<IPluginManager>(new ContractPlugins());
                services.AddSingleton<IOptionsMonitor<PluginNodeMessageOptions>>(new StaticOptionsMonitor<PluginNodeMessageOptions>(new()));
                services.AddNodeHandshakeServices();
                services.AddReverseProxy().LoadFromMemory([], []);
                services.AddSingleton<NodeDashboardAgent>();
            });
            const string candidate = "SpeedLimit:\n  Value: new\n";
            var prepare = Message("configuration.prepare", new PrepareConfigurationRequest("contract-operation", "v",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate))), candidate));
            var invalid = Message("configuration.prepare", new PrepareConfigurationRequest("invalid", "v", "bad", candidate))
                with { CorrelationId = "invalid" };
            var nack = await Check(host, invalid, 422, false, "ack", "error", "errorCode", "messageKey", "deploymentId");
            Assert.False(nack.GetProperty("ack").GetBoolean());
            Assert.Equal(NodeConfigurationErrors.Digest, nack.GetProperty("error").GetString());
            Assert.Equal(NodeConfigurationErrors.Digest, nack.GetProperty("errorCode").GetString());
            foreach (var operation in new[] { "configuration.commit", "configuration.rollback" })
            {
                var missing = Message(operation, new DistributionCommandRequest("missing")) with { CorrelationId = "missing" };
                var rejected = await Check(host, missing, 409, false, "ack", "error", "errorCode", "messageKey");
                Assert.False(rejected.GetProperty("ack").GetBoolean());
                Assert.Equal(rejected.GetProperty("errorCode").GetString(), rejected.GetProperty("error").GetString());
            }
            await Check(host, prepare, 200, true, "ack", "deploymentId", "nodeId");
            await Check(host, Message("configuration.result", prepare), 200, true, "ack", "deploymentId");
            await Check(host, Message("configuration.commit", new DistributionCommandRequest("contract-operation")), 200, true, "ack");
            Assert.Contains("new", File.ReadAllText(SharedData.ConfigFilePath));
            await Check(host, Message("configuration.rollback", new DistributionCommandRequest("contract-operation")), 200, true, "ack");
            Assert.Contains("old", File.ReadAllText(SharedData.ConfigFilePath));
            await Check(host, Message("node.status", new { }), 200, true, "nodeId", "state");
            await Check(host, Message("node.dashboard", new { }), 200, true, "nodeId", "security");
            var handshake = new NodeHandshake("contract", "sender", Guid.NewGuid().ToString("N"), "Controller",
                DateTimeOffset.UtcNow, "test", [2], new(null, new string('a', 64), null),
                new(NodeHandshakeMetadataService.ComputeDigest([]), []));
            await Check(host, Message("node.handshake", handshake), 200, true, "local", "registration");
            await Check(host, Message("node.handshake", new { }), 400, false, "errorCode", "messageKey");
            File.Move(SharedData.ConfigFilePath, SharedData.ConfigFilePath + ".saved");
            await Check(host, Message("node.handshake", handshake), 503, false, "errorCode", "messageKey");
            File.Move(SharedData.ConfigFilePath + ".saved", SharedData.ConfigFilePath);
            foreach (var op in new[] { "configuration.prepare", "configuration.commit", "configuration.rollback", "configuration.result" })
                await Check(host, Message(op, new { }), 400, false, "errorCode", "messageKey");

            var inbox = Path.Combine(host.Directory, "inbox");
            Directory.Move(inbox, inbox + ".saved");
            File.WriteAllText(inbox, "blocked directory");
            foreach (var command in new[] { prepare, Message("configuration.commit", new DistributionCommandRequest("contract-operation")),
                Message("configuration.rollback", new DistributionCommandRequest("contract-operation")), Message("configuration.result", prepare) })
                await Check(host, command, 409, false, "errorCode", "messageKey");
        }
        finally { (root as IDisposable)?.Dispose(); SharedData.ConfigFilePath = oldPath; }
    }

    [Fact]
    public async Task MergeAndSyncReturnActualBusinessAndDiskFailureContracts()
    {
        await using var host = await NodeContractHttpHost.Start(configure: (services, h) =>
        {
            services.AddSingleton<RuntimeStateStore>();
            services.AddSingleton<IRuntimeStateStore>(sp => sp.GetRequiredService<RuntimeStateStore>());
            services.AddHostedService(sp => sp.GetRequiredService<RuntimeStateStore>());
            services.AddSingleton<RuntimeStateSyncReceiver>();
        });
        var now = DateTimeOffset.UtcNow;
        var mutation = new RuntimeStateMutation("ip.blocked", "192.0.2.1", "sender", 1, now, now.AddMinutes(5), false,
            JsonSerializer.SerializeToElement("blocked"));
        await Check(host, Message("runtime.merge", mutation), 200, true);
        await Check(host, Message("runtime.merge", mutation with { Domain = "not.granted" }), 403, false, "errorCode", "messageKey");
        await Check(host, Message("runtime.merge", mutation with { Key = "192.0.2.2" }), 503, false, "errorCode", "messageKey");
        host.Runtime.MaxStateEntries = 100;
        host.Runtime.Peers["sender"].ProtocolVersion = 2;
        host.Runtime.Peers["sender"].DurableSyncEnabled = true;
        var batch = new RuntimeStateSyncBatch("contract", "sender", "receiver", Guid.NewGuid().ToString("N"), 1, [mutation], "");
        batch = batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        await Check(host, Message("runtime.sync", batch), 200, true, "sequence", "digest", "status");
        await Check(host, Message("runtime.sync", new { }), 400, false, "errorCode", "messageKey");
        var path = Path.Combine(host.Runtime.StateDirectory, "runtime-sync");
        Directory.Move(path, path + ".saved");
        File.WriteAllText(path, "blocked directory");
        await Check(host, Message("runtime.sync", batch), 503, false, "errorCode", "messageKey");
    }

    [Fact]
    public async Task PluginAndCcOperationsUseRealHandlersOverHttp()
    {
        var plugins = new ContractPlugins();
        var rule = new AdvancedCcRule { Id = "captcha", Action = CcAction.Captcha, Threshold = 2 };
        var statistics = DispatchProxy.Create<IStatisticService, CcCaptchaReleaseTests.Rules>();
        ((CcCaptchaReleaseTests.Rules)(object)statistics).Rule = rule;
        await using var host = await NodeContractHttpHost.Start(configure: (services, h) =>
        {
            CcReleaseTestKeys.Configure(h.Runtime);
            h.Runtime.MaxStateEntries = 100;
            h.Runtime.Peers["sender"].Send = new() { AllowedDomains = ["cc.decision"], AllowUngroupedKeys = true };
            h.Protocol.Peers["sender"].CcCaptchaReleaseGrants = [new("captcha", "host:a.example")];
            services.AddSingleton(statistics);
            services.AddSingleton<CcCaptchaReleaseIssuer>();
            LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Register(services);
            services.AddSingleton<CcBanLog>();
            services.AddSingleton<CcDecisionService>();
            services.AddSingleton<CcExecutionQueryService>();
            services.AddSingleton<IPluginManager>(plugins);
            services.AddSingleton<IOptionsMonitor<PluginNodeMessageOptions>>(new StaticOptionsMonitor<PluginNodeMessageOptions>(new()
            {
                Enabled = true, Groups = new() { ["workers"] = ["receiver"] },
                Grants = new() { ["sample"] = new() { ReceiveGroups = ["workers"], AllowedSenders = ["sender"] } }
            }));
            services.AddSingleton<IOptionsMonitor<DistributionOptions>>(new StaticOptionsMonitor<DistributionOptions>(new()));
            services.AddHttpClient();
            services.AddSingleton<PluginNodeMessageService>();
        });
        var payload = new PluginNodeMessagePayload("sample", "job", 1, "workers", JsonSerializer.SerializeToElement(new { }));
        await Check(host, Message("plugin.message", payload), 200, true, "ack", "pluginId", "schemaVersion");
        Assert.Equal(1, plugins.Plugin.Calls);
        await Check(host, Message("plugin.message", payload with { SchemaVersion = 2 }), 422, false, "errorCode", "messageKey");
        var receipts = Path.Combine(host.Directory, "plugin-messages", "incoming");
        Directory.Move(receipts, receipts + ".saved");
        File.WriteAllText(receipts, "blocked directory");
        await Check(host, Message("plugin.message", payload), 503, false, "errorCode", "messageKey");
        Assert.Equal(1, plugins.Plugin.Calls);
        var policyKey = CcRuleStateKey.Create(rule, "192.0.2.87", "host:a.example");
        var target = new CcCaptchaTarget(rule.Id, "host:a.example", "192.0.2.87", policyKey, CcPolicyGeneration.Initial);
        var release = new CcCaptchaReleaseRequest(target, new string('a', 64), DateTimeOffset.UtcNow);
        await Check(host, Message(CcCaptchaReleaseIssuer.Operation, release), 200, true, "fence", "issuerApplied", "allNodesConfirmed");
        await Check(host, Message(CcCaptchaReleaseIssuer.Operation, new { }), 400, false, "errorCode", "messageKey");
        var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), target.Scope, target.Ip, policyKey, CcPolicyGeneration.Initial);
        await Check(host, Message(CcExecutionQueryService.Operation, new CcExecutionQuery(policy, new string('b', 64))),
            200, true, "execution");
        await Check(host, Message(CcExecutionQueryService.Operation, new { }), 400, false, "errorCode", "messageKey");
        var generations = Path.Combine(host.Runtime.StateDirectory, "cc-policy-generations.jsonl");
        File.Move(generations, generations + ".saved");
        Directory.CreateDirectory(generations);
        var next = host.App.Services.GetRequiredService<IRuntimeStateStore>().GetCcPolicyGeneration(policyKey);
        await Check(host, Message(CcCaptchaReleaseIssuer.Operation, release with
            { Target = target with { Expected = next }, ChallengeId = new string('b', 64) }), 503, false, "errorCode", "messageKey");
    }

    private static NodeMessage Message(string operation, object payload) => new(2, "contract", "sender", "receiver",
        Guid.NewGuid().ToString("D"), operation == "plugin.message" ? "sample" : "contract-operation", operation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
        JsonSerializer.SerializeToElement(payload, NodeMessageAuthentication.JsonOptions));

    private static async Task<JsonElement> Check(NodeContractHttpHost host, NodeMessage message, int status, bool success, params string[] retained)
    {
        using var response = await host.Send(message);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{message.Type}: expected {status}, got {(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        var body = json.RootElement;
        Assert.Equal(success, body.GetProperty("success").GetBoolean());
        Assert.Equal(message.MessageId, body.GetProperty("messageId").GetString());
        Assert.Equal(message.CorrelationId, body.GetProperty("operationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Object, body.GetProperty("messageArgs").ValueKind);
        Assert.Equal(status is 429 or 503, body.GetProperty("retryable").GetBoolean());
        foreach (var field in retained) Assert.True(body.TryGetProperty(field, out _), $"{message.Type}: missing {field}: {text}");
        if (!success)
            Assert.Equal("node.errors." + body.GetProperty("errorCode").GetString(), body.GetProperty("messageKey").GetString());
        Assert.DoesNotContain(host.Directory, text);
        return body.Clone();
    }

    private sealed class ContractPlugin : LyWafPluginBase, IPluginNodeMessageHandler
    {
        public int Calls;
        public override PluginMetadata Metadata => new() { Id = "sample", Name = "Contract", Version = "1.0",
            RequestedPermissions = PluginPermission.NodeMessaging, NodeMessages = [new("job", 1, ["workers"], true, true)] };
        public Task<PluginNodeHandlerResult> HandleNodeMessageAsync(PluginNodeReceivedMessage message, CancellationToken token)
        { Calls++; return Task.FromResult(new PluginNodeHandlerResult(true)); }
    }

    // Only the plugin registry is a fixture; dispatch, authorization and durable receipts are production services.
    private sealed class ContractPlugins : IPluginManager
    {
        public ContractPlugin Plugin = new();
        public ILyWafPlugin? GetPlugin(string id) => id == "sample" ? Plugin : id == "analysis" ? new LyWaf.Plugins.Sys.AnalysisPlugin() : null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [Plugin];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [];
        public bool IsSystemPlugin(string id) => false;
        public Task EnablePluginAsync(string id) => Task.CompletedTask;
        public Task DisablePluginAsync(string id) => Task.CompletedTask;
        public Task ReloadPluginAsync(string id) => Task.CompletedTask;
        public PluginState GetPluginState(string id) => PluginState.Running;
        public bool IsPermissionGranted(string id, PluginPermission permission) => id is "sample" or "analysis";
    }
}
