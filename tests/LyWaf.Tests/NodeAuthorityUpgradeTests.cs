using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class NodeSetupCryptoTests
{
    [Fact]
    public async Task AuthenticatedLegacyUpgradeIsDurableIdempotentAndOnlyAppliesAfterRestart()
    {
        var fixture = await LegacyNode();
        var before = fixture.Store.Status();
        Assert.True(await fixture.Store.StageAuthorityUpgradeAsync(fixture.Config, fixture.Request, fixture.Remote, fixture.Proof, default));
        var pending = fixture.Store.Status();
        Assert.True(pending.AuthorityUpgradePending);
        Assert.True(pending.RestartRequired);
        Assert.Equal(before.NodeId, pending.NodeId);
        Assert.Empty(fixture.Store.Active!.Node!.Credential.ReleasePublicKeyPem);
        Assert.Empty(fixture.Store.ChallengeSecret);
        Assert.False(await fixture.Store.StageAuthorityUpgradeAsync(fixture.Config, fixture.Request, fixture.Remote, fixture.Proof, default));
        Assert.Equal(pending.Revision, fixture.Store.Status().Revision);
        var restarted = new NodeSetupStore(fixture.NodeRoot);
        Assert.False(restarted.Status().AuthorityUpgradePending);
        Assert.False(restarted.Status().RestartRequired);
        Assert.Equal(fixture.Proof.Authority!.ChallengeSecret, restarted.ChallengeSecret);
        Assert.Equal(fixture.Config.NodeId, restarted.Active!.Node!.Credential.NodeId);
        var transport = fixture.Store.Active.Node.Credential;
        Assert.Equal(transport.KeyId, restarted.Active.Node.Credential.KeyId);
        Assert.Equal(transport.Secret, restarted.Active.Node.Credential.Secret);
        var status = JsonSerializer.Serialize(pending);
        Assert.DoesNotContain(fixture.Proof.Authority.ChallengeSecret, status);
        Assert.DoesNotContain(transport.Secret, status);
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(fixture.NodeRoot, "setup.json")));
        using var services = Services(fixture.NodeRoot);
        var runtime = services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        Assert.Equal(fixture.Proof.Authority.ReleasePublicKeyPem, runtime.CcReleaseAuthorities[fixture.Config.ControllerNodeId]);
        Assert.Empty(runtime.CcReleasePrivateKeyPem);
    }

    [Fact]
    public async Task PendingOperatorEditIsNotOverwrittenByBackgroundUpgrade()
    {
        var fixture = await LegacyNode();
        await fixture.Store.ConfigureAsync("Node", "https://replacement.example/", "", default);
        var revision = fixture.Store.Status().Revision;
        Assert.False(await fixture.Store.StageAuthorityUpgradeAsync(fixture.Config, fixture.Request, fixture.Remote, fixture.Proof, default));
        Assert.Equal(revision, fixture.Store.Status().Revision);
        var disk = new NodeSetupStore(fixture.NodeRoot);
        Assert.Equal("https://replacement.example/", disk.Status().ControllerUrl);
        Assert.Empty(disk.Active!.Node!.Credential.ReleasePublicKeyPem);
    }

    [Theory]
    [InlineData("public-key")]
    [InlineData("challenge-secret")]
    [InlineData("trust-domain")]
    public async Task PinnedAuthorityCannotBeSilentlyRotatedEvenByAnAuthenticatedController(string field)
    {
        var fixture = await LegacyNode();
        await fixture.Store.StageAuthorityUpgradeAsync(fixture.Config, fixture.Request, fixture.Remote, fixture.Proof, default);
        var running = new NodeSetupStore(fixture.NodeRoot);
        var previous = File.ReadAllText(Path.Combine(fixture.NodeRoot, "setup.json"));
        var authority = Change(fixture.Proof.Authority!, field);
        var resigned = NodeHubMembership.Create(fixture.Controller, fixture.Request, fixture.Remote, fixture.Proof.KeyId,
            DateTimeOffset.UtcNow, authority);
        await Assert.ThrowsAsync<InvalidDataException>(() => running.StageAuthorityUpgradeAsync(fixture.Config,
            fixture.Request, fixture.Remote, resigned, default));
        Assert.Equal(previous, File.ReadAllText(Path.Combine(fixture.NodeRoot, "setup.json")));
        Assert.False(running.Status().RestartRequired);
    }

    [Theory]
    [InlineData("public-key")]
    [InlineData("challenge-secret")]
    [InlineData("trust-domain")]
    public async Task ChangedAuthorityWithoutFreshSignatureCannotTouchDisk(string field)
    {
        var fixture = await LegacyNode();
        var previous = File.ReadAllText(Path.Combine(fixture.NodeRoot, "setup.json"));
        var invalid = fixture.Proof with { Authority = Change(fixture.Proof.Authority!, field) };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.StageAuthorityUpgradeAsync(fixture.Config,
            fixture.Request, fixture.Remote, invalid, default));
        Assert.Equal(previous, File.ReadAllText(Path.Combine(fixture.NodeRoot, "setup.json")));
        Assert.False(fixture.Store.Status().RestartRequired);
    }

    [Fact]
    public async Task CancelledUpgradeLeavesLegacyCredentialIntact()
    {
        var fixture = await LegacyNode();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.StageAuthorityUpgradeAsync(fixture.Config,
            fixture.Request, fixture.Remote, fixture.Proof, cancelled.Token));
        Assert.Empty(new NodeSetupStore(fixture.NodeRoot).Active!.Node!.Credential.ReleasePublicKeyPem);
        Assert.False(fixture.Store.Status().RestartRequired);
    }

    private static NodeHubAuthority Change(NodeHubAuthority authority, string field) => field switch
    {
        "public-key" => authority with { ReleasePublicKeyPem = NodeSetupCrypto.PublicKey(NodeSetupCrypto.NewPrivateKey()) },
        "challenge-secret" => authority with { ChallengeSecret = new string('c', 64) },
        _ => authority with { ReleaseTrustDomain = "other-domain" }
    };
    private sealed record UpgradeFixture(NodeSetupStore Store, string NodeRoot, NodeCommunicationOptions Config,
        NodeCommunicationOptions Controller, NodeMessage Request, NodeHandshake Remote, NodeHubMembershipProof Proof);

    private async Task<UpgradeFixture> LegacyNode()
    {
        var controllerRoot = Path.Combine(root, "controller");
        var controller = new NodeSetupStore(controllerRoot);
        await controller.ConfigureAsync("Controller", null, null, default);
        var issued = await controller.CreateCredentialAsync("legacy-node", default);
        var credential = NodeSetupStore.DecodeCredential(issued.Credential);
        var legacy = credential with { ReleasePublicKeyPem = "", ChallengeSecret = "", ReleaseTrustDomain = "" };
        var encoded = "lywaf_node_" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(legacy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var nodeRoot = Path.Combine(root, "node");
        await new NodeSetupStore(nodeRoot).ConfigureAsync("Node", "https://controller.example/", encoded, default);
        var store = new NodeSetupStore(nodeRoot);
        var config = new NodeCommunicationOptions { Enabled = true, Mode = "Node", Role = "Node", ClusterId = legacy.ClusterId,
            NodeId = legacy.NodeId, ControllerNodeId = legacy.ControllerNodeId, Peers = new()
            { [legacy.ControllerNodeId] = new() { Role = "Controller", ActiveKeyId = legacy.KeyId,
                Keys = new() { [legacy.KeyId] = legacy.Secret }, AllowedOperations = ["node.handshake"] } } };
        var control = new NodeCommunicationOptions { Enabled = true, Mode = "Controller", Role = "Controller", ClusterId = legacy.ClusterId,
            NodeId = legacy.ControllerNodeId, Peers = new()
            { [legacy.NodeId] = new() { Role = "Node", ActiveKeyId = legacy.KeyId,
                Keys = new() { [legacy.KeyId] = legacy.Secret }, AllowedOperations = NodeProtocolOperations.All.ToList() } } };
        var now = DateTimeOffset.UtcNow;
        var request = new NodeMessage(2, legacy.ClusterId, legacy.NodeId, legacy.ControllerNodeId, Guid.NewGuid().ToString("D"),
            "upgrade", "node.handshake", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
        var remote = new NodeHandshake(legacy.ClusterId, legacy.ControllerNodeId, Guid.NewGuid().ToString("N"), "Controller", now, "1", [2]);
        var authority = new NodeSetupStore(controllerRoot).ControllerAuthority(control)!;
        var proof = NodeHubMembership.Create(control, request, remote, legacy.KeyId, now, authority);
        return new(store, nodeRoot, config, control, request, remote, proof);
    }
}
