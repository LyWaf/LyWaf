using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class PluginNodeMessageTests
{
    [Theory]
    [InlineData("mode")]
    [InlineData("role")]
    [InlineData("sender-role")]
    [InlineData("revoked")]
    [InlineData("operation")]
    [InlineData("grant")]
    [InlineData("sender")]
    [InlineData("target")]
    [InlineData("origin")]
    public async Task RelayRequiresControllerAdmissionAndExplicitHostGrant(string denied)
    {
        var config = _sender.CurrentValue;
        config.Mode = "Controller"; config.Role = "Controller";
        config.Peers["edge"].Role = "Node";
        config.Peers["edge"].AllowedOperations.Add(PluginNodeMessageService.RelayOperation);
        config.Peers["destination"] = Peer(); config.Peers["destination"].Role = "Node";
        var grant = _policy.CurrentValue.Grants["sample"];
        grant.RelayGroups = ["workers"]; grant.AllowedSenders = ["edge"];
        _policy.CurrentValue.Groups["workers"] = ["destination"];
        var payload = new PluginNodeMessagePayload("sample", "job", 1, "workers", Data);
        var target = "destination";
        if (denied == "mode") config.Mode = "";
        if (denied == "role") config.Role = "Node";
        if (denied == "sender-role") config.Peers["edge"].Role = "Controller";
        if (denied == "revoked") config.Peers["edge"].AdmissionState = "Revoked";
        if (denied == "operation") config.Peers["edge"].AllowedOperations.Remove(PluginNodeMessageService.RelayOperation);
        if (denied == "grant") grant.RelayGroups = [];
        if (denied == "sender") grant.AllowedSenders = [];
        if (denied == "target") target = "control";
        if (denied == "origin") payload = payload with { OriginNodeId = "victim" };
        var message = new NodeMessage(2, "cluster", "edge", "control", Id(), "sample", PluginNodeMessageService.RelayOperation,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            JsonSerializer.SerializeToElement(new PluginNodeRelayPayload(target, payload), Json));
        Assert.NotNull((await Service(true).HandleRelayAsync(message, default)).Error);
        Assert.Equal(0, _transport.Calls);
        Assert.Equal(0, _manager.Plugin.Calls);
    }

    [Fact]
    public async Task OnlyManagedControllerCanAssertForwardedPluginOrigin()
    {
        var message = Message();
        var payload = message.Payload.Deserialize<PluginNodeMessagePayload>(Json)! with { OriginNodeId = "original" };
        message = message with { Payload = JsonSerializer.SerializeToElement(payload, Json) };
        Assert.Equal(403, (await Service().HandleAuthenticatedAsync(message, default)).Error?.StatusCode);
        _receiver.CurrentValue.Mode = "Node";
        _receiver.CurrentValue.Role = "Node";
        _receiver.CurrentValue.ControllerNodeId = "control";
        _receiver.CurrentValue.Peers["control"].Role = "Controller";
        Assert.Equal(403, (await Service().HandleAuthenticatedAsync(message, default)).Error?.StatusCode);
        _policy.CurrentValue.Grants["sample"].AllowedSenders = ["original"];
        string? origin = null;
        _manager.Plugin.Handle = (received, _) =>
        {
            origin = received.SenderNodeId;
            return Task.FromResult(new PluginNodeHandlerResult(true));
        };
        // A new ID is required because a rejected business message has a durable receipt.
        message = message with { MessageId = Id() };
        Assert.True((await Service().HandleAuthenticatedAsync(message, default)).Reply!.Ack);
        Assert.Equal("original", origin);
    }

    [Fact]
    public async Task TwoManagedNodesRelayOverControllerSocketsWithoutPeerCredentialsOrHttpFallback()
    {
        var hosts = new List<WebApplication>();
        try
        {
            var setup = new NodeSetupStore(Path.Combine(_directory, "hub", "setup"));
            await setup.ConfigureAsync("Controller", null, null, default);
            var inviteA = await setup.CreateCredentialAsync("a", default);
            var inviteB = await setup.CreateCredentialAsync("b", default);
            var aCredential = NodeSetupStore.DecodeCredential(inviteA.Credential);
            var bCredential = NodeSetupStore.DecodeCredential(inviteB.Credential);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var url = $"http://127.0.0.1:{port}/";
            await new NodeSetupStore(Path.Combine(_directory, "a", "setup")).ConfigureAsync("Node", url, inviteA.Credential, default);
            await new NodeSetupStore(Path.Combine(_directory, "b", "setup")).ConfigureAsync("Node", url, inviteB.Credential, default);
            var hubManager = new Manager(); var aManager = new Manager(); var bManager = new Manager();
            var hub = BuildRelayHost("hub", port, hubManager);
            var a = BuildRelayHost("a", 0, aManager);
            var b = BuildRelayHost("b", 0, bManager);
            hosts.AddRange([hub, a, b]);
            foreach (var host in hosts) await host.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var hubConfig = hub.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
            while (!a.Services.GetRequiredService<NodeHubMembership>().IsFresh(hubConfig.ClusterId, hubConfig.NodeId)
                || !b.Services.GetRequiredService<NodeHubMembership>().IsFresh(hubConfig.ClusterId, hubConfig.NodeId))
                await Task.Delay(10, deadline.Token);
            foreach (var host in hosts)
            {
                var policy = host.Services.GetRequiredService<IOptionsMonitor<PluginNodeMessageOptions>>().CurrentValue;
                policy.Enabled = true;
                policy.Groups["workers"] = [bCredential.NodeId];
                policy.Grants["sample"] = new() { SendGroups = ["workers"], ReceiveGroups = ["workers"],
                    RelayGroups = ["workers"], AllowedSenders = [aCredential.NodeId] };
            }
            Assert.Single(a.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.Peers);
            Assert.False(a.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.Peers.ContainsKey(bCredential.NodeId));
            string? origin = null;
            bManager.Plugin.Handle = (received, _) =>
            {
                origin = received.SenderNodeId;
                return Task.FromResult(new PluginNodeHandlerResult(true));
            };
            var client = a.Services.GetRequiredService<PluginNodeMessageService>().Bind("sample");
            var id = Id();
            var delivery = Assert.Single(await client.SendAsync("job", 1, "workers", Data, id, deadline.Token));
            Assert.True(delivery.Ack, delivery.ErrorCode);
            Assert.Equal(bCredential.NodeId, delivery.NodeId);
            Assert.Equal(aCredential.NodeId, origin);
            Assert.Equal(0, hubManager.Plugin.Calls);
            Assert.Equal(0, aManager.Plugin.Calls);
            var audits = Directory.GetFiles(hubConfig.StateDirectory, "*.json", SearchOption.AllDirectories)
                .Where(path => path.Contains("plugin-messages" + Path.DirectorySeparatorChar + "outgoing"))
                .Select(File.ReadAllText).ToArray();
            var audit = JsonSerializer.Deserialize<PluginNodeAudit>(Assert.Single(audits))!;
            Assert.Equal(aCredential.NodeId, audit.OriginNodeId);
            Assert.DoesNotContain(audits, text => text.Contains("SECRET_PAYLOAD"));
            Assert.True(Assert.Single(await client.SendAsync("job", 1, "workers", Data, id, deadline.Token)).Ack);
            Assert.Equal(1, bManager.Plugin.Calls);

            var relayId = Id();
            var payload = new PluginNodeMessagePayload("sample", "job", 1, "workers", Data);
            var envelope = new NodeMessage(2, hubConfig.ClusterId, aCredential.NodeId, hubConfig.NodeId, relayId,
                "sample", PluginNodeMessageService.RelayOperation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30),
                JsonSerializer.SerializeToElement(new PluginNodeRelayPayload(bCredential.NodeId, payload), Json));
            var messaging = hub.Services.GetRequiredService<PluginNodeMessageService>();
            Assert.True((await messaging.HandleRelayAsync(envelope, deadline.Token)).Reply!.Ack);
            var restartedMessaging = ActivatorUtilities.CreateInstance<PluginNodeMessageService>(hub.Services);
            Assert.True((await restartedMessaging.HandleRelayAsync(envelope, deadline.Token)).Reply!.Ack);
            Assert.Equal(2, bManager.Plugin.Calls);
            var changed = envelope with { Payload = JsonSerializer.SerializeToElement(
                new PluginNodeRelayPayload(bCredential.NodeId, payload with { Data = JsonSerializer.SerializeToElement(new { changed = true }) }), Json) };
            Assert.Equal("node_plugin_message_conflict", (await messaging.HandleRelayAsync(changed, deadline.Token)).Reply!.ErrorCode);
            Assert.Equal(2, bManager.Plugin.Calls);

            var relayPolicy = hub.Services.GetRequiredService<IOptionsMonitor<PluginNodeMessageOptions>>().CurrentValue;
            relayPolicy.Grants["sample"].RelayGroups = [];
            Assert.False(Assert.Single(await client.SendAsync("job", 1, "workers", Data, Id(), deadline.Token)).Ack);
            Assert.Equal(2, bManager.Plugin.Calls);
            relayPolicy.Grants["sample"].RelayGroups = ["workers"];
            hubConfig.Peers[bCredential.NodeId].AdmissionState = "Revoked";
            Assert.False(Assert.Single(await client.SendAsync("job", 1, "workers", Data, Id(), deadline.Token)).Ack);
            Assert.Equal(2, bManager.Plugin.Calls);
            hubConfig.Peers[bCredential.NodeId].AdmissionState = "Active";
            bManager.Plugin.Handle = (_, _) => throw new IOException("handler failed after starting");
            var unknown = envelope with { MessageId = Id() };
            Assert.False((await messaging.HandleRelayAsync(unknown, deadline.Token)).Reply!.Ack);
            Assert.Equal("node_plugin_outcome_unknown", (await restartedMessaging.HandleRelayAsync(unknown, deadline.Token)).Reply!.ErrorCode);
            Assert.Equal(3, bManager.Plugin.Calls);
        }
        finally
        {
            foreach (var host in hosts.AsEnumerable().Reverse()) { await host.StopAsync(); await host.DisposeAsync(); }
        }
    }

    private WebApplication BuildRelayHost(string name, int port, Manager manager)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddNodeSetup(builder.Services, Path.Combine(_directory, name, "setup"));
        builder.Services.AddOptions<NodeCommunicationOptions>().Bind(builder.Configuration.GetSection("NodeCommunication"));
        builder.Services.AddOptions<DistributionOptions>().Bind(builder.Configuration.GetSection("Distribution"));
        builder.Services.AddOptions<RuntimeStateOptions>().Bind(builder.Configuration.GetSection("RuntimeState"));
        builder.Services.AddSingleton<IPluginManager>(manager);
        builder.Services.AddSingleton<INodeHandshakeConfigurationSource, RelayRevision>();
        builder.Services.AddSingleton<NodeIdentityService>();
        builder.Services.AddHostedService(s => s.GetRequiredService<NodeIdentityService>());
        builder.Services.AddSingleton<NodeSocketConnections>();
        builder.Services.AddSingleton<NodeSocketProtocol>();
        builder.Services.AddPluginNodeMessaging(builder.Configuration);
        builder.Services.AddNodeHandshakeServices();
        builder.Services.AddHostedService<NodeSocketConnector>();
        // A successful test cannot use HTTP even accidentally: any factory request fails immediately.
        builder.Services.AddSingleton<IHttpClientFactory>(new NoRelayHttp());
        var app = builder.Build();
        app.UseWebSockets(); app.MapNodeCommunication(port); app.MapNodeSocket(port);
        return app;
    }

    private sealed class RelayRevision : INodeHandshakeConfigurationSource
    {
        public Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token) => Task.FromResult(
            new NodeConfigurationRevision(null, new string('A', 64), null));
    }
    private sealed class NoRelayHttp : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Managed plugin messages must not dial HTTP");
    }
}
