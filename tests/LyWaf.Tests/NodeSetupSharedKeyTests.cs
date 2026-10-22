using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSetupSharedKeyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-shared-key-" + Guid.NewGuid().ToString("N"));

    private async Task<NodeSetupStore> Controller()
    {
        await new NodeSetupStore(root).ConfigureAsync("Controller", null, null, default);
        return new NodeSetupStore(root);
    }

    [Fact]
    public async Task ExistingAndFutureNodesShareTheKeyButKeepTheirOwnIdentity()
    {
        var store = await Controller();
        var original = NodeSetupStore.DecodeCredential((await store.CreateCredentialAsync("first", default)).Credential);
        Assert.True(await store.SetSharedCommunicationKeyAsync(new string('a', 64), default));
        var first = NodeSetupStore.DecodeCredential(await store.IssuePeerCredentialAsync(original.NodeId, default));
        var second = NodeSetupStore.DecodeCredential((await store.CreateCredentialAsync("second", default)).Credential);
        Assert.Equal(new string('a', 64), first.Secret);
        Assert.Equal(first.Secret, second.Secret);
        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Equal(original.NodeId, first.NodeId);
        Assert.NotEqual(first.NodeId, second.NodeId);
        Assert.NotEqual(original.Secret, first.Secret);
        var revision = store.Status().Revision;
        Assert.False(await store.SetSharedCommunicationKeyAsync(new string('a', 64), default));
        Assert.Equal(revision, store.Status().Revision);
        var restored = new NodeSetupStore(root);
        Assert.True(restored.Status().HasSharedCommunicationKey);
        Assert.Equal(first, NodeSetupStore.DecodeCredential(await restored.IssuePeerCredentialAsync(first.NodeId, default)));
        await restored.SetSharedCommunicationKeyAsync(new string('b', 64), default);
        var rotated = NodeSetupStore.DecodeCredential(await restored.IssuePeerCredentialAsync(first.NodeId, default));
        Assert.NotEqual(first.KeyId, rotated.KeyId);
        Assert.Equal(new string('b', 64), rotated.Secret);
    }

    [Fact]
    public async Task DeletionRemovesCredentialsButPreservesOnlyHistoricalProvenance()
    {
        var store = await Controller();
        var key = NodeSetupStore.DecodeCredential((await store.CreateCredentialAsync("edge", default)).Credential);
        await store.DeletePeerAsync(key.NodeId, default);
        Assert.Empty(store.Peers());
        var protocol = new NodeCommunicationOptions();
        NodeSetupConfiguration.Apply(protocol, store);
        Assert.DoesNotContain(key.NodeId, protocol.Peers.Keys);
        Assert.Contains(key.NodeId, protocol.HistoricalNodeIds);
        var runtime = new RuntimeStateOptions();
        NodeSetupConfiguration.Apply(runtime, store, new NodeHubMembership());
        Assert.DoesNotContain(key.NodeId, runtime.Peers.Keys);
        Assert.True(runtime.HubSourceIsKnown!(key.NodeId));
        Assert.DoesNotContain(key.Secret, await File.ReadAllTextAsync(Path.Combine(root, "setup.json")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetPeerEnabledAsync(key.NodeId, true, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.IssuePeerCredentialAsync(key.NodeId, default));
        var restored = new NodeSetupStore(root);
        Assert.Empty(restored.Peers());
        var newKey = NodeSetupStore.DecodeCredential((await restored.CreateCredentialAsync("edge", default)).Credential);
        Assert.NotEqual(key.NodeId, newKey.NodeId);
    }

    [Fact]
    public async Task PendingControllerChangesMustBeAppliedBeforeLiveRevocationOrKeyRotation()
    {
        var store = await Controller();
        var peer = (await store.CreateCredentialAsync("edge", default)).Peer;
        await store.ConfigureAsync("Controller", null, null, default, synchronizeRuntimeState: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeletePeerAsync(peer.NodeId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetSharedCommunicationKeyAsync(new string('a', 64), default));
        Assert.Single(store.Peers());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("                                ")]
    [InlineData("01234567890123456789012345678901\n")]
    public async Task InvalidKeysDoNotChangePersistentState(string key)
    {
        var store = await Controller();
        var revision = store.Status().Revision;
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetSharedCommunicationKeyAsync(key, default));
        Assert.Equal(revision, store.Status().Revision);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
