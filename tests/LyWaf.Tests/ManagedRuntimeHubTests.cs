using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using LyWaf.Services.Captcha;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class ManagedRuntimeHubTests
{
    [Fact]
    public async Task ThreeHostsAggregateOverControllerSocketsAndIgnoreForeignCounterClaims()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-managed-runtime-" + Guid.NewGuid().ToString("N"));
        var hosts = new List<WebApplication>();
        try
        {
            var setup = new NodeSetupStore(Path.Combine(root, "controller", "setup"));
            await setup.ConfigureAsync("Controller", null, null, default);
            var invitationA = await setup.CreateCredentialAsync("a", default);
            var invitationB = await setup.CreateCredentialAsync("b", default);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var controlPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var controlUrl = $"http://127.0.0.1:{controlPort}/";
            await new NodeSetupStore(Path.Combine(root, "a", "setup")).ConfigureAsync("Node", controlUrl, invitationA.Credential, default);
            await new NodeSetupStore(Path.Combine(root, "b", "setup")).ConfigureAsync("Node", controlUrl, invitationB.Credential, default);
            var controller = Build(Path.Combine(root, "controller"), controlPort);
            var a = Build(Path.Combine(root, "a"), 0);
            var b = Build(Path.Combine(root, "b"), 0);
            hosts.AddRange([controller, a, b]);
            foreach (var host in hosts) await host.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var control = controller.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
            var nodeA = NodeSetupStore.DecodeCredential(invitationA.Credential);
            var nodeB = NodeSetupStore.DecodeCredential(invitationB.Credential);
            await Until(() => a.Services.GetRequiredService<NodeHubMembership>().IsFresh(control.ClusterId, control.NodeId)
                && b.Services.GetRequiredService<NodeHubMembership>().IsFresh(control.ClusterId, control.NodeId), deadline.Token);
            var runtimeA = a.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            Assert.Equal(control.NodeId, Assert.Single(runtimeA.Peers).Key);
            Assert.Equal(2, controller.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue.Peers.Count);
            Assert.All(controller.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue.Peers.Values,
                peer => Assert.Equal("https://node-socket.invalid/", peer.Endpoint));
            var window = DateTimeOffset.UtcNow;
            var stores = hosts.Select(host => host.Services.GetRequiredService<RuntimeStateStore>()).ToArray();
            stores[1].IncrementWindow("cc.access", "counter", window, TimeSpan.FromMinutes(5), 3);
            stores[2].IncrementWindow("cc.access", "counter", window, TimeSpan.FromMinutes(5), 5);
            stores[0].IncrementWindow("cc.access", "counter", window, TimeSpan.FromMinutes(5), 2);
            await Until(() => stores.All(store => store.GetWindowValue("cc.access", "counter", window) == 10), deadline.Token);

            // B can upload an aggregate it has observed, but it cannot increase A's component at the controller.
            var sample = stores[2].ExportPage(null, 32).Items.Single(item => item.Domain == "cc.access");
            var foreign = sample.CounterComponents!.Keys.Single(component =>
                RuntimeStateReplicationAuthorization.TryComponentSource(component, out var source) && source == nodeA.NodeId);
            var parts = new Dictionary<string, long>(sample.CounterComponents) { [foreign] = 9999 };
            var raw = sample with { CounterComponents = parts };
            var sender = new RuntimeStateSyncSender(b.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>());
            await Until(() => sender.ReadPending(control.ClusterId, nodeB.NodeId, control.NodeId) is null, deadline.Token);
            var batch = sender.Prepare(control.ClusterId, nodeB.NodeId, control.NodeId, [raw]);
            var now = DateTimeOffset.UtcNow;
            var envelope = new NodeMessage(2, control.ClusterId, nodeB.NodeId, control.NodeId, Guid.NewGuid().ToString("D"), batch.Epoch,
                RuntimeStateSyncProtocol.Operation, now, now.AddMinutes(1), JsonSerializer.SerializeToElement(batch, NodeMessageAuthentication.JsonOptions));
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl.TrimEnd('/') + NodeMessageAuthentication.Prefix + envelope.Type)
                { Content = new ByteArrayContent(body) };
            NodeMessageAuthentication.Sign(request, envelope, nodeB.KeyId, nodeB.Secret, body);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await NodeControlTransport.SendAsync(b.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue,
                b.Services.GetRequiredService<NodeSocketConnections>(), control.NodeId, http, request, deadline.Token);
            response.EnsureSuccessStatusCode();
            var ack = JsonSerializer.Deserialize<RuntimeStateSyncAck>(await response.Content.ReadAsByteArrayAsync(), RuntimeStateSyncProtocol.Json)!;
            Assert.Equal(batch.Digest, ack.Digest);
            Assert.Equal("Applied", ack.Status);
            Assert.Equal(10, stores[0].GetWindowValue("cc.access", "counter", window));
            Assert.Equal(10, stores[1].GetWindowValue("cc.access", "counter", window));
            Assert.Equal(10, stores[2].GetWindowValue("cc.access", "counter", window));
        }
        finally
        {
            foreach (var host in hosts.AsEnumerable().Reverse()) { await host.StopAsync(); await host.DisposeAsync(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static WebApplication Build(string directory, int port, AdvancedCcRule? rule = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddNodeSetup(builder.Services, Path.Combine(directory, "setup"));
        builder.Services.AddOptions<NodeCommunicationOptions>().Bind(builder.Configuration.GetSection("NodeCommunication"));
        builder.Services.AddOptions<RuntimeStateOptions>().Bind(builder.Configuration.GetSection("RuntimeState"));
        builder.Services.AddOptions<PluginNodeMessageOptions>();
        builder.Services.AddSingleton(DispatchProxy.Create<IPluginManager, NodeHandshakeMetadataTests.ManagerProxy>());
        builder.Services.AddSingleton<INodeHandshakeConfigurationSource, Revision>();
        builder.Services.AddSingleton<NodeIdentityService>();
        builder.Services.AddHostedService(s => s.GetRequiredService<NodeIdentityService>());
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<NodeSocketConnections>();
        builder.Services.AddSingleton<NodeSocketProtocol>();
        builder.Services.AddNodeHandshakeServices();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        if (rule is not null)
        {
            var statistics = DispatchProxy.Create<IStatisticService, CcCohortHttpTests.Rules>();
            ((CcCohortHttpTests.Rules)(object)statistics).Rule = rule;
            builder.Services.AddSingleton(statistics);
            builder.Services.AddSingleton<CcBanLog>();
            builder.Services.AddSingleton<CcCohortState>();
            builder.Services.AddSingleton<CcDecisionService>();
            builder.Services.AddSingleton<CcPeerQueryClient>();
            builder.Services.AddSingleton<CcExecutionQueryService>();
            builder.Services.AddSingleton<CcExecutionRelayService>();
            builder.Services.AddSingleton<CcCaptchaReleaseIssuer>();
            builder.Services.AddSingleton<CcCaptchaReleaseClient>();
            builder.Services.AddLyWafCaptcha(builder.Configuration);
        }
        builder.Services.AddHostedService<RuntimeStateReplicator>();
        builder.Services.AddHostedService<NodeSocketConnector>();
        var app = builder.Build();
        app.UseWebSockets();
        app.MapNodeCommunication(port);
        app.MapNodeSocket(port);
        return app;
    }

    private sealed class Revision : INodeHandshakeConfigurationSource
    {
        public Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token) => Task.FromResult(
            new NodeConfigurationRevision(null, new string('A', 64), null));
    }

    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }
}
