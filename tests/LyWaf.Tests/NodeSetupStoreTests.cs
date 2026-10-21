using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSetupStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-node-setup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavingUnchangedRoleDoesNotWriteOrRequireRestart()
    {
        var seed = new NodeSetupStore(root);
        await seed.ConfigureAsync("Controller", null, null, default);
        var store = new NodeSetupStore(root);
        var before = store.Status();
        var path = Path.Combine(root, "setup.json");
        var bytes = File.ReadAllBytes(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var saved = await store.ConfigureAsync("Controller", null, null, default);
        Assert.Equal(before.Revision, saved.Revision);
        Assert.False(saved.RestartRequired);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before.NodeId, saved.NodeId);
    }

    [Fact]
    public async Task RepeatedPendingSaveCannotApplyRoleWithoutRestart()
    {
        var store = new NodeSetupStore(root);
        var first = await store.ConfigureAsync("Controller", null, null, default);
        var again = await store.ConfigureAsync("Controller", null, null, default);
        Assert.Equal(first.Revision, again.Revision);
        Assert.True(again.RestartRequired);
        Assert.Null(store.Active);
    }

    [Fact]
    public async Task RevertingPendingRoleRestoresRunningConfigurationWithoutRestart()
    {
        await new NodeSetupStore(root).ConfigureAsync("Controller", null, null, default);
        var store = new NodeSetupStore(root);
        var peer = await store.CreateCredentialAsync("edge", default);
        var before = store.Status();
        await store.ConfigureAsync("Standalone", null, null, default);
        Assert.True(store.Status().RestartRequired);
        var reverted = await store.ConfigureAsync("Controller", null, null, default);
        Assert.False(reverted.RestartRequired);
        Assert.Equal(before.NodeId, reverted.NodeId);
        Assert.Equal(before.ClusterId, reverted.ClusterId);
        Assert.Equal(peer.Peer.NodeId, Assert.Single(store.Active!.Controller!.Peers).NodeId);
        Assert.Equal(reverted.Revision, store.Active.Revision);
        await store.SetPeerEnabledAsync(peer.Peer.NodeId, false, default);
        Assert.False(Assert.Single(store.Active.Controller.Peers).Enabled);
        Assert.False(store.Status().RestartRequired);
    }

    [Fact]
    public async Task NormalizedNodeUrlAndBlankCredentialAreNoOpButActualEndpointChangeRemainsPending()
    {
        var controller = new NodeSetupStore(Path.Combine(root, "controller"));
        await controller.ConfigureAsync("Controller", null, null, default);
        var credential = await controller.CreateCredentialAsync("edge", default);
        var nodeRoot = Path.Combine(root, "node");
        await new NodeSetupStore(nodeRoot).ConfigureAsync("Node", "https://controller.example", credential.Credential, default);
        var node = new NodeSetupStore(nodeRoot);
        var before = node.Status();
        var saved = await node.ConfigureAsync("Node", " https://controller.example/ ", "", default);
        Assert.Equal(before.Revision, saved.Revision);
        Assert.False(saved.RestartRequired);
        var changed = await node.ConfigureAsync("Node", "https://other.example", "", default);
        Assert.True(changed.RestartRequired);
        Assert.Equal("https://controller.example/", node.Active!.Node!.ControllerUrl);
        var reverted = await node.ConfigureAsync("Node", "https://controller.example", "", default);
        Assert.False(reverted.RestartRequired);
        Assert.Equal(before.NodeId, reverted.NodeId);
    }

    [Fact]
    public async Task RoleChangesPersistButDoNotSilentlyChangeRunningIdentity()
    {
        var store = new NodeSetupStore(root);
        Assert.Equal("Standalone", store.Status().Mode);
        Assert.Null(store.Active);
        var configured = await store.ConfigureAsync("Controller", null, null, default);
        Assert.True(configured.RestartRequired);
        Assert.Equal("Standalone", configured.ActiveMode);
        Assert.Null(store.Active);
        var restarted = new NodeSetupStore(root);
        Assert.Equal("Controller", restarted.Status().ActiveMode);
        Assert.False(restarted.Status().RestartRequired);
        Assert.Equal(configured.NodeId, restarted.Status().NodeId);
        Assert.Equal(configured.ClusterId, restarted.Status().ClusterId);
        await restarted.ConfigureAsync("Standalone", null, null, default);
        Assert.Equal("Controller", restarted.Status().ActiveMode);
        Assert.True(restarted.Status().RestartRequired);
        Assert.Equal("Standalone", new NodeSetupStore(root).Status().ActiveMode);
    }

    [Fact]
    public async Task EachNodeHasIndependentCredentialAndSecretsAreNeverInStatus()
    {
        var store = new NodeSetupStore(root);
        await store.ConfigureAsync("Controller", null, null, default);
        store = new NodeSetupStore(root);
        var first = await store.CreateCredentialAsync("first", default);
        var second = await store.CreateCredentialAsync("second", default);
        var a = NodeSetupStore.DecodeCredential(first.Credential);
        var b = NodeSetupStore.DecodeCredential(second.Credential);
        Assert.NotEqual(a.NodeId, b.NodeId);
        Assert.NotEqual(a.Secret, b.Secret);
        Assert.NotEqual(a.KeyId, b.KeyId);
        Assert.Equal(a.ClusterId, b.ClusterId);
        Assert.Equal(a.ControllerNodeId, b.ControllerNodeId);
        Assert.Equal(2, store.Active!.Controller!.Peers.Count);
        Assert.False(store.Status().RestartRequired);
        var status = JsonSerializer.Serialize(new { settings = store.Status(), peers = store.Peers() });
        Assert.DoesNotContain(a.Secret, status);
        Assert.DoesNotContain(first.Credential, status);
        Assert.DoesNotContain("Secret", status);
        Assert.Equal(2, new NodeSetupStore(root).Peers().Count);
    }

    [Fact]
    public async Task NodeOnlyNeedsUrlAndCredentialAndCanPreserveAnUnreadableKey()
    {
        var controller = new NodeSetupStore(Path.Combine(root, "controller"));
        await controller.ConfigureAsync("Controller", null, null, default);
        var invitation = await controller.CreateCredentialAsync("edge", default);
        var node = new NodeSetupStore(Path.Combine(root, "node"));
        var status = await node.ConfigureAsync("Node", "https://controller.example:7030", invitation.Credential, default);
        Assert.Equal("https://controller.example:7030/", status.ControllerUrl);
        Assert.True(status.HasCredential);
        Assert.Equal(invitation.Peer.NodeId, status.NodeId);
        status = await node.ConfigureAsync("Node", "https://replacement.example:7030/", "", default);
        Assert.True(status.HasCredential);
        var restored = new NodeSetupStore(Path.Combine(root, "node"));
        Assert.Equal(NodeSetupStore.DecodeCredential(invitation.Credential), restored.Active!.Node!.Credential);
        Assert.Equal("https://replacement.example:7030/", restored.Status().ControllerUrl);
    }

    [Fact]
    public async Task ConcurrentInvitationsDoNotLosePeers()
    {
        var store = new NodeSetupStore(root);
        await store.ConfigureAsync("Controller", null, null, default);
        store = new NodeSetupStore(root);
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(n => store.CreateCredentialAsync("node-" + n, default)));
        Assert.Equal(30, store.Peers().Count);
        Assert.Equal(30, results.Select(item => item.Peer.NodeId).Distinct().Count());
        Assert.Equal(30, new NodeSetupStore(root).Peers().Count);
        Assert.False(store.Status().RestartRequired);
    }

    [Fact]
    public async Task ExternalEditsCannotBeOverwrittenByAStaleProcess()
    {
        var stale = new NodeSetupStore(root);
        var writer = new NodeSetupStore(root);
        await writer.ConfigureAsync("Controller", null, null, default);
        await Assert.ThrowsAsync<IOException>(() => stale.ConfigureAsync("Standalone", null, null, default));
        Assert.Equal("Controller", new NodeSetupStore(root).Status().Mode);
    }

    [Fact]
    public async Task PeerRevocationIsIndependentAndRefreshesOnlyTheExistingRole()
    {
        var store = new NodeSetupStore(root);
        await store.ConfigureAsync("Controller", null, null, default);
        store = new NodeSetupStore(root);
        var a = await store.CreateCredentialAsync("a", default);
        var b = await store.CreateCredentialAsync("b", default);
        await store.SetPeerEnabledAsync(a.Peer.NodeId, false, default);
        Assert.False(store.Active!.Controller!.Peers.Single(peer => peer.NodeId == a.Peer.NodeId).Enabled);
        Assert.True(store.Active!.Controller!.Peers.Single(peer => peer.NodeId == b.Peer.NodeId).Enabled);
        Assert.False(store.Status().RestartRequired);
    }

    [Fact]
    public async Task CancelledSaveDoesNotChangeDiskOrDesiredState()
    {
        var store = new NodeSetupStore(root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfigureAsync("Controller", null, null, cancelled.Token));
        Assert.Null(new NodeSetupStore(root).Active);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("127.0.0.1:7030")]
    [InlineData("https://0.0.0.0:7030")]
    [InlineData("https://[::]:7030")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/subpath")]
    [InlineData("https://example.com/?key=secret")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("ftp://example.com")]
    public void InvalidControllerAddressesAreRejected(string url) => Assert.Throws<ArgumentException>(() => NodeSetupStore.NormalizeUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData("password")]
    [InlineData("lywaf_node_!!!!")]
    [InlineData("lywaf_node_eyJ2ZXJzaW9uIjo5fQ")]
    public void InvalidCredentialDoesNotLeakInput(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => NodeSetupStore.DecodeCredential(value));
        Assert.Equal("Invalid node credential", error.Message);
    }

    [Fact]
    public void CorruptStateFailsClosed()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "setup.json"), "{\"version\":1,\"revision\":4,\"mode\":\"Node\"}");
        Assert.Throws<InvalidDataException>(() => new NodeSetupStore(root));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
