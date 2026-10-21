using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class ManagedRuntimeHubTests
{
    [Fact]
    public async Task LegacyNodeReceivesSignedUpgradeOverExistingSocketAndKeepsIdentityAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-managed-upgrade-" + Guid.NewGuid().ToString("N"));
        var hosts = new List<WebApplication>();
        try
        {
            var controllerRoot = Path.Combine(root, "controller");
            var nodeRoot = Path.Combine(root, "node");
            var seed = new NodeSetupStore(Path.Combine(controllerRoot, "setup"));
            await seed.ConfigureAsync("Controller", null, null, default);
            var invitation = await seed.CreateCredentialAsync("legacy-node", default);
            var original = NodeSetupStore.DecodeCredential(invitation.Credential);
            var legacy = original with { ReleasePublicKeyPem = "", ChallengeSecret = "", ReleaseTrustDomain = "" };
            var encoded = "lywaf_node_" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(legacy,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await new NodeSetupStore(Path.Combine(nodeRoot, "setup")).ConfigureAsync("Node", $"http://127.0.0.1:{port}/", encoded, default);
            var controller = Build(controllerRoot, port);
            var node = Build(nodeRoot, 0);
            hosts.AddRange([controller, node]);
            foreach (var host in hosts) await host.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var store = node.Services.GetRequiredService<NodeSetupStore>();
            await Until(() => store.Status().AuthorityUpgradePending
                && node.Services.GetRequiredService<NodeHubMembership>().IsFresh(original.ClusterId, original.ControllerNodeId), deadline.Token);
            Assert.Equal(original.NodeId, store.Status().NodeId);
            Assert.Equal(new[] { original.NodeId, original.ControllerNodeId }.Order(StringComparer.Ordinal),
                node.Services.GetRequiredService<NodeHubMembership>().ExpectedCcNodes(original.ClusterId, original.ControllerNodeId));
            Assert.True(store.Status().RestartRequired);
            Assert.Empty(store.Active!.Node!.Credential.ReleasePublicKeyPem);
            Assert.Empty(node.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue.CcReleaseAuthorities);
            var disk = new NodeSetupStore(Path.Combine(nodeRoot, "setup"));
            Assert.Equal(original.ReleasePublicKeyPem, disk.Active!.Node!.Credential.ReleasePublicKeyPem);
            Assert.Equal(original.Secret, disk.Active.Node.Credential.Secret);
            var revision = disk.Status().Revision;
            var local = await node.Services.GetRequiredService<NodeHandshakeMetadataService>().CaptureAsync(deadline.Token);
            var nodeConfig = node.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            async Task<HttpResponseMessage> SendHandshake(NodeHandshake metadata, bool socket)
            {
                var now = DateTimeOffset.UtcNow;
                var message = new NodeMessage(2, original.ClusterId, original.NodeId, original.ControllerNodeId,
                    Guid.NewGuid().ToString("D"), "session-end-test", "node.handshake", now, now.AddMinutes(1),
                    JsonSerializer.SerializeToElement(metadata, NodeMessageAuthentication.JsonOptions));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}" + NodeMessageAuthentication.Prefix + "node.handshake")
                    { Content = new ByteArrayContent(bytes) };
                NodeMessageAuthentication.Sign(request, message, original.KeyId, original.Secret, bytes);
                return socket ? await NodeControlTransport.SendAsync(nodeConfig, node.Services.GetRequiredService<NodeSocketConnections>(),
                    original.ControllerNodeId, http, request, deadline.Token) : await http.SendAsync(request, deadline.Token);
            }
            using (var legacyClient = await SendHandshake(local with { ControlCapabilities = null }, true))
            {
                legacyClient.EnsureSuccessStatusCode();
                var exchange = JsonSerializer.Deserialize<NodeHandshakeExchange>(await legacyClient.Content.ReadAsByteArrayAsync(),
                    NodeMessageAuthentication.JsonOptions)!;
                Assert.NotNull(exchange.Membership);
                Assert.Null(exchange.Membership.Authority);
                Assert.Null(exchange.Membership.ExpectedCcNodes);
            }
            using (var forgedBoot = await SendHandshake(local with { Ending = true, BootId = Guid.NewGuid().ToString("N") }, true))
                Assert.Equal(HttpStatusCode.Forbidden, forgedBoot.StatusCode);
            using (var noSocket = await SendHandshake(local with { Ending = true }, false))
                Assert.Equal(HttpStatusCode.Forbidden, noSocket.StatusCode);
            Assert.False(controller.Services.GetRequiredService<NodeRegistrationObserver>().Get(original.NodeId)!.Handshake.Ending);
            await node.StopAsync(); await node.DisposeAsync(); hosts.Remove(node);
            Assert.True(controller.Services.GetRequiredService<NodeRegistrationObserver>().Get(original.NodeId)!.Handshake.Ending);
            var restarted = Build(nodeRoot, 0);
            hosts.Add(restarted);
            await restarted.StartAsync();
            await Until(() => restarted.Services.GetRequiredService<NodeHubMembership>().IsFresh(original.ClusterId, original.ControllerNodeId), deadline.Token);
            var current = restarted.Services.GetRequiredService<NodeSetupStore>();
            Assert.Equal(revision, current.Status().Revision);
            Assert.False(current.Status().RestartRequired);
            Assert.False(current.Status().AuthorityUpgradePending);
            Assert.Equal(original.NodeId, restarted.Services.GetRequiredService<RuntimeStateStore>().GetStatus().NodeId);
            var runtime = restarted.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            Assert.Equal(original.ReleasePublicKeyPem, runtime.CcReleaseAuthorities[original.ControllerNodeId]);
            Assert.Empty(runtime.CcReleasePrivateKeyPem);
            Assert.Contains(local.BootId, controller.Services.GetRequiredService<NodeRegistrationObserver>().Get(original.NodeId)!.RetiredBootIds);
            using var oldBoot = await SendHandshake(local, false);
            Assert.Equal(HttpStatusCode.BadRequest, oldBoot.StatusCode);
        }
        finally
        {
            foreach (var host in hosts.AsEnumerable().Reverse()) { await host.StopAsync(); await host.DisposeAsync(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
