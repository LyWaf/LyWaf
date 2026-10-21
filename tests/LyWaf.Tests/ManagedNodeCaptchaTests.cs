using System.Net;
using System.Net.Sockets;
using LyWaf.Services.Captcha;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class ManagedRuntimeHubTests
{
    [Fact]
    public async Task PrivateNodeCaptchaRuleReleasesThroughControllerWithoutCopyingItsDefinition()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-node-captcha-" + Guid.NewGuid().ToString("N"));
        var hosts = new List<WebApplication>();
        try
        {
            var controlRoot = Path.Combine(root, "controller"); var nodeRoot = Path.Combine(root, "node");
            var seed = new NodeSetupStore(Path.Combine(controlRoot, "setup"));
            await seed.ConfigureAsync("Controller", null, null, default);
            var invitation = await seed.CreateCredentialAsync("captcha-node", default);
            var credential = NodeSetupStore.DecodeCredential(invitation.Credential);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await new NodeSetupStore(Path.Combine(nodeRoot, "setup")).ConfigureAsync("Node", $"http://127.0.0.1:{port}/", invitation.Credential, default);
            var rule = new AdvancedCcRule { Id = "private-captcha", Scope = CcScope.Node, Action = CcAction.Captcha };
            var controller = Build(controlRoot, port, new AdvancedCcRule { Id = "unrelated", Action = CcAction.Block });
            var node = Build(nodeRoot, 0, rule);
            hosts.AddRange([controller, node]);
            foreach (var host in hosts) await host.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Until(() => node.Services.GetRequiredService<NodeHubMembership>().IsFresh(credential.ClusterId, credential.ControllerNodeId), deadline.Token);
            var runtime = node.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            Assert.True(runtime.Enabled);
            Assert.Empty(runtime.CcReleasePrivateKeyPem);
            var target = new CcCaptchaTarget(rule.Id, "node:" + credential.NodeId, "192.0.2.7",
                CcRuleStateKey.Create(rule, "192.0.2.7", "node:" + credential.NodeId), CcPolicyGeneration.Initial);
            var tokens = node.Services.GetRequiredService<ISharedCaptchaStore>();
            var token = "r1." + Guid.NewGuid().ToString("N");
            await tokens.PutAsync(token, new CaptchaService.CaptchaEntry { Mode = "math", Answer = "42", ClientIp = target.PolicyKey }, deadline.Token);
            var consumed = await tokens.TakeAsync(token, deadline.Token);
            Assert.Equal("42", consumed!.Answer);
            Assert.Null(await tokens.TakeAsync(token, deadline.Token));
            var client = node.Services.GetRequiredService<CcCaptchaReleaseClient>();
            var confirmation = await client.ReleaseAsync(target, token, deadline.Token);
            Assert.True(confirmation.IssuerSigned);
            Assert.True(confirmation.LocalApplied);
            Assert.False(confirmation.AllNodesConfirmed);
            Assert.Equal(credential.ControllerNodeId, confirmation.IssuerNodeId);
            var local = node.Services.GetRequiredService<RuntimeStateStore>();
            var authority = controller.Services.GetRequiredService<RuntimeStateStore>();
            Assert.Equal(1, local.GetCcPolicyGeneration(target.PolicyKey).Version);
            Assert.Equal(local.GetCcPolicyGeneration(target.PolicyKey), authority.GetCcPolicyGeneration(target.PolicyKey));
            var repeated = await client.ReleaseAsync(target, token, deadline.Token);
            Assert.True(repeated.LocalApplied);
            Assert.Equal(1, authority.GetCcPolicyGeneration(target.PolicyKey).Version);
            var otherKey = CcRuleStateKey.Create(rule, target.Ip, "node:other");
            Assert.Equal(CcPolicyGeneration.Initial, authority.GetCcPolicyGeneration(otherKey));
        }
        finally
        {
            foreach (var host in hosts.AsEnumerable().Reverse()) { await host.StopAsync(); await host.DisposeAsync(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
