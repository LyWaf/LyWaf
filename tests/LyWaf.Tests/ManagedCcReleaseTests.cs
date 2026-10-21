using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Captcha;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagedCcReleaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "managed-cc-release-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly FileCaptchaAuthority files;
    private readonly RuntimeStateStore store;
    private readonly StaticOptionsMonitor<RuntimeStateOptions> runtime;
    private readonly NodeCommunicationOptions communication;
    private readonly IStatisticService statistics;
    private readonly CcCaptchaReleaseIssuer issuer;
    private readonly AdvancedCcRule rule = new() { Id = "private-rule", Scope = CcScope.Node, Action = CcAction.Captcha };

    public ManagedCcReleaseTests()
    {
        files = new(Options.Create(new CaptchaStoreOptions { StateDirectory = Path.Combine(root, "captcha"), ClusterId = "cluster", AuthorityNodeId = "hub" }), clock);
        runtime = new StaticOptionsMonitor<RuntimeStateOptions>(CcReleaseTestKeys.Configure(new()
            { Enabled = true, NodeId = "hub", StateDirectory = Path.Combine(root, "runtime") }));
        store = new(runtime);
        communication = new() { Enabled = true, Mode = "Controller", Role = "Controller", NodeId = "hub", ClusterId = "cluster",
            Peers = new() { ["a"] = new() { Role = "Node", AllowedOperations = [CcCaptchaReleaseIssuer.Operation] } } };
        statistics = DispatchProxy.Create<IStatisticService, Rules>();
        issuer = new(new StaticOptionsMonitor<NodeCommunicationOptions>(communication),
            runtime, statistics, store, new Receipts(files));
    }

    private CcCaptchaTarget Target(string scope = "node:a", AdvancedCcRule? policy = null) => new(rule.Id, scope, "192.0.2.7",
        CcRuleStateKey.Create(policy ?? rule, "192.0.2.7", scope), CcPolicyGeneration.Initial);
    private async Task<string> Consume(CcCaptchaTarget target, string consumer = "a", string? subject = null)
    {
        var token = "r1." + Guid.NewGuid().ToString("N");
        await files.PutAsync(token, new CaptchaService.CaptchaEntry { Mode = "math", Answer = "42",
            ClientIp = subject ?? target.Expected.StateKey(target.PolicyKey) }, clock.Now, 300000, default);
        Assert.NotNull(await files.TakeAsync(token, default, consumer));
        return token;
    }
    private Task<IResult> Release(CcCaptchaTarget target, string token, AdvancedCcRule? snapshot, HttpContext? context = null)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new CcCaptchaReleaseRequest(target, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), now, snapshot);
        return issuer.HandleAsync(new(2, "cluster", "a", "hub", Guid.NewGuid().ToString("D"), "release", CcCaptchaReleaseIssuer.Operation,
            now, now.AddMinutes(1), JsonSerializer.SerializeToElement(request, NodeMessageAuthentication.JsonOptions)), context);
    }

    [Fact]
    public async Task PrivateNodeRuleNeedsNoControllerCopyButStillRequiresRealConsumption()
    {
        var target = Target();
        var other = Target("node:b");
        store.Set("cc.triggered", other.PolicyKey, "other-node-penalty");
        var missing = await Release(target, "r1." + Guid.NewGuid().ToString("N"), rule);
        Assert.Equal(403, ((IStatusCodeHttpResult)missing).StatusCode);
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(target.PolicyKey));
        var token = await Consume(target);
        var accepted = await Release(target, token, rule);
        Assert.Equal(200, ((IStatusCodeHttpResult)accepted).StatusCode);
        var body = JsonSerializer.SerializeToElement(((IValueHttpResult)accepted).Value, NodeMessageAuthentication.JsonOptions);
        var response = body.Deserialize<CcCaptchaReleaseResponse>(NodeMessageAuthentication.JsonOptions)!;
        Assert.NotNull(response.Fence.CcReleaseProof);
        Assert.False(response.AllNodesConfirmed);
        Assert.Equal(1, store.GetCcPolicyGeneration(target.PolicyKey).Version);
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(other.PolicyKey));
        Assert.True(store.TryGet<string>("cc.triggered", other.PolicyKey, out _));
        Assert.Equal(200, ((IStatusCodeHttpResult)await Release(target, token, rule)).StatusCode);
        Assert.Equal(1, store.GetCcPolicyGeneration(target.PolicyKey).Version);
    }

    [Theory]
    [InlineData("consumer", 403)]
    [InlineData("subject", 403)]
    [InlineData("expired", 403)]
    [InlineData("foreign-node", 400)]
    [InlineData("shared-scope", 400)]
    [InlineData("cluster-scope", 400)]
    [InlineData("policy-key", 409)]
    [InlineData("disabled", 409)]
    [InlineData("large", 400)]
    [InlineData("draining", 403)]
    [InlineData("revoked", 403)]
    public async Task ConsumptionAndNamespaceBindingsCannotBeBypassed(string fault, int status)
    {
        var target = Target(fault == "foreign-node" ? "node:b" : "node:a");
        if (fault == "shared-scope") { rule.Scope = CcScope.SiteGroup; target = Target("host:shared.example"); }
        if (fault == "cluster-scope") { rule.Scope = CcScope.Cluster; target = Target("cluster:shared"); }
        if (fault == "policy-key") target = target with { PolicyKey = Target("node:b").PolicyKey };
        if (fault == "disabled") rule.Enabled = false;
        if (fault == "large") rule.Name = new string('x', 65536);
        var token = await Consume(target, fault == "consumer" ? "b" : "a", fault == "subject" ? "unrelated-subject" : null);
        if (fault == "expired") clock.Now = clock.Now.AddSeconds(121);
        if (fault == "draining") communication.Peers["a"].AdmissionState = "Draining";
        if (fault == "revoked") communication.Peers["a"].AdmissionState = "Revoked";
        Assert.Equal(status, ((IStatusCodeHttpResult)await Release(target, token, rule)).StatusCode);
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(target.PolicyKey));
    }

    [Fact]
    public async Task SharedRuleUsesControllerDefinitionAndReceiptInsteadOfCallerDefinition()
    {
        rule.Scope = CcScope.SiteGroup;
        ((Rules)(object)statistics).Rule = rule;
        var target = Target("host:shared.example");
        var token = await Consume(target);
        Assert.Equal(200, ((IStatusCodeHttpResult)await Release(target, token, null)).StatusCode);
        Assert.Equal(1, store.GetCcPolicyGeneration(target.PolicyKey).Version);
    }

    [Fact]
    public async Task UnknownSharedRuleCannotBeReleasedWithConsumptionAlone()
    {
        rule.Scope = CcScope.SiteGroup;
        var target = Target("host:shared.example");
        var token = await Consume(target);
        Assert.Equal(409, ((IStatusCodeHttpResult)await Release(target, token, null)).StatusCode);
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(target.PolicyKey));
    }

    [Fact]
    public async Task DisabledSynchronizationKeepsLocalReleaseIndependentOfRemoteRuleLookup()
    {
        var localOptions = new StaticOptionsMonitor<RuntimeStateOptions>(new() { Enabled = false, NodeId = "a",
            StateDirectory = Path.Combine(root, "local-disabled") });
        using var localStore = new RuntimeStateStore(localOptions);
        var noLookup = DispatchProxy.Create<IStatisticService, NoRuleLookup>();
        using var client = new CcCaptchaReleaseClient(localOptions, new StaticOptionsMonitor<NodeCommunicationOptions>(new()
            { Enabled = true, Mode = "Node", Role = "Node", NodeId = "a", ClusterId = "cluster" }), localStore, noLookup);
        var confirmation = await client.ReleaseAsync(Target(), "local-token", default);
        Assert.True(confirmation.LocalApplied);
        Assert.False(confirmation.IssuerSigned);
        Assert.Equal(1, localStore.GetCcPolicyGeneration(Target().PolicyKey).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptFailureOrCancellationAfterVerificationCannotPublishFence(bool cancel)
    {
        var target = Target();
        using var cancellation = new CancellationTokenSource();
        var receipts = new DelegateReceipts(() =>
        {
            if (!cancel) throw new CaptchaStoreUnavailableException();
            cancellation.Cancel();
            return Task.FromResult(true);
        });
        var failing = new CcCaptchaReleaseIssuer(new StaticOptionsMonitor<NodeCommunicationOptions>(communication), runtime, statistics, store, receipts);
        var now = DateTimeOffset.UtcNow;
        var request = new CcCaptchaReleaseRequest(target, new string('a', 64), now, rule);
        var message = new NodeMessage(2, "cluster", "a", "hub", Guid.NewGuid().ToString("D"), "failure", CcCaptchaReleaseIssuer.Operation,
            now, now.AddMinutes(1), JsonSerializer.SerializeToElement(request, NodeMessageAuthentication.JsonOptions));
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing.HandleAsync(message, context));
        else Assert.Equal(503, ((IStatusCodeHttpResult)await failing.HandleAsync(message, context)).StatusCode);
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(target.PolicyKey));
    }

    [Fact]
    public async Task CancellationCannotPublishARelease()
    {
        var target = Target(); var token = await Consume(target);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Release(target, token, rule,
            new DefaultHttpContext { RequestAborted = cancelled.Token }));
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(target.PolicyKey));
    }

    private sealed class Receipts(FileCaptchaAuthority authority) : ICcCaptchaConsumption
    {
        public Task<bool> WasConsumedAsync(string digest, string node, string subject, CancellationToken token)
            => authority.WasConsumedAsync(digest, node, subject, token);
    }
    private sealed class DelegateReceipts(Func<Task<bool>> query) : ICcCaptchaConsumption
    {
        public Task<bool> WasConsumedAsync(string digest, string node, string subject, CancellationToken token) => query();
    }
    public class Rules : DispatchProxy
    {
        public AdvancedCcRule? Rule;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IStatisticService.GetAdvancedCcRule) => Rule?.Id == (string)args![0]! ? Rule : null,
            nameof(IStatisticService.GetAdvancedCcRules) => Rule is null ? new List<AdvancedCcRule>() : new List<AdvancedCcRule> { Rule },
            _ => throw new NotSupportedException()
        };
    }
    public class NoRuleLookup : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Local release must not inspect remote rule metadata");
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { files.Dispose(); store.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
