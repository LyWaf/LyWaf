using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

public sealed class RuntimeStateSyncReceiver(IOptionsMonitor<RuntimeStateOptions> options, RuntimeStateStore store)
{
    private sealed record State(string ClusterId, string SenderNodeId, string ReceiverNodeId,
        string Epoch, long Sequence, Dictionary<long, string> Digests,
        DateTimeOffset? LastAcceptedAt = null, string? BootstrapDigest = null);
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<RuntimeStateSyncAck> AcceptAsync(RuntimeStateSyncBatch batch, CancellationToken token)
    {
        RuntimeStateSyncProtocol.Validate(batch);
        var current = options.CurrentValue;
        foreach (var mutation in batch.Mutations) store.ValidateForSync(mutation);
        var applied = RuntimeStateHub.ProjectIncoming(current, batch.SenderNodeId, batch.Mutations);
        if (current.NodeId != batch.ReceiverNodeId
            || !current.Peers.TryGetValue(batch.SenderNodeId, out var peer) || !peer.DurableSyncEnabled
            || RuntimeStateReplicationAuthorization.AuthorizeReceive(current, batch.SenderNodeId, applied, durableSync: true) is not null)
            throw new UnauthorizedAccessException("Runtime sync receive is not authorized");
        token.ThrowIfCancellationRequested();
        var files = new RuntimeStateSyncFiles(current.StateDirectory, current);
        var path = files.PathFor("in", batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
        using var held = files.Lock(path);
        var state = files.Read<State>(path);
        var durable = store.GetDurableSyncCheckpoint(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
        // An exact durable head may still be confirmed without writing the stopped journal.
        if (batch.Bootstrap is not null || durable is null || durable.Epoch != batch.Epoch
            || durable.Sequence != batch.Sequence || durable.Digest != batch.Digest)
            store.EnsureSyncWriterAvailable();
        if (batch.Bootstrap is not null) return await BootstrapAsync(batch, peer, files, path, state, token);
        RuntimeStateSyncAck Reply(string status, long sequence, string digest = "") => new(
            batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, state?.Epoch ?? durable?.Epoch ?? batch.Epoch,
            sequence, digest, status, sequence + 1);
        if (!store.SyncJournalHealthy) return Reply("SnapshotRequired", state?.Sequence ?? 0);
        if (state is not null)
        {
            if (state.ClusterId != batch.ClusterId || state.SenderNodeId != batch.SenderNodeId || state.ReceiverNodeId != batch.ReceiverNodeId
                || state.Sequence < 0 || (state.Sequence > 0 && state.Digests.Count == 0) || state.Digests.Count > RuntimeStateSyncProtocol.MaximumHistory
                || state.Digests.Count > state.Sequence
                || Enumerable.Range(0, state.Digests.Count).Any(i => !state.Digests.ContainsKey(state.Sequence - i)))
                throw new InvalidDataException("Invalid runtime sync receiver checkpoint");
            // A receipt without the corresponding durable journal fence must never ACK a duplicate.
            if (durable is null || durable.Epoch != state.Epoch || durable.Sequence < state.Sequence
                || durable.Sequence == state.Sequence && durable.Digest != (state.Sequence == 0 ? state.BootstrapDigest : state.Digests[state.Sequence]))
                return Reply("SnapshotRequired", state.Sequence);
            if (state.LastAcceptedAt is null || UtcNow() - state.LastAcceptedAt > TimeSpan.FromSeconds(Math.Clamp(peer.SyncOfflineWindowSeconds, 1, 86400)))
                return Reply("SnapshotRequired", state.Sequence);
        }
        else if (durable is { Sequence: > 1 }) return Reply("SnapshotRequired", durable.Sequence);
        if ((state?.Epoch ?? durable?.Epoch ?? batch.Epoch) != batch.Epoch)
            return Reply("SnapshotRequired", state?.Sequence ?? 0);
        var sequence = state?.Sequence ?? 0;
        if (batch.Sequence <= sequence)
            return !state!.Digests.TryGetValue(batch.Sequence, out var previousDigest)
                ? Reply("SnapshotRequired", sequence)
                : previousDigest == batch.Digest
                ? Reply("Applied", batch.Sequence, batch.Digest) : Reply("Conflict", sequence);
        if (batch.Sequence != sequence + 1) return Reply("Gap", sequence);
        if (durable?.Sequence == batch.Sequence && durable.Digest != batch.Digest) return Reply("Conflict", sequence);

        // A crash after journal fsync but before receipt commit must not reapply an old value.
        if (durable?.Sequence != batch.Sequence)
            await store.MergeDurablyAsync(batch, token, applied);
        token.ThrowIfCancellationRequested();
        var digests = state is null ? new Dictionary<long, string>() : new(state.Digests);
        digests.Add(batch.Sequence, batch.Digest);
        if (digests.Count > RuntimeStateSyncProtocol.MaximumHistory) digests.Remove(digests.Keys.Min());
        files.Save(path, new State(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, digests, UtcNow()), token);
        return new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Digest);
    }

    private async Task<RuntimeStateSyncAck> BootstrapAsync(RuntimeStateSyncBatch batch, RuntimeStatePeerOptions peer,
        RuntimeStateSyncFiles files, string path, State? state, CancellationToken token)
    {
        if (batch.Bootstrap!.Phase == "probe")
        {
            var fence = store.GetDurableSyncCheckpoint(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
            return new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, state?.Epoch ?? fence?.Epoch ?? batch.Epoch,
                state?.Sequence ?? fence?.Sequence ?? 0, "", "SnapshotRequired");
        }
        if (!peer.AllowBootstrapExport || !store.SyncJournalHealthy)
            throw new UnauthorizedAccessException("Bootstrap export is not authorized or journal is unhealthy");
        var request = batch.Bootstrap!;
        RuntimeStateSyncAck Conflict() => new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId,
            state?.Epoch ?? batch.Epoch, state?.Sequence ?? 0, "", "Conflict");
        var scope = RuntimeStateBootstrapScope.From(options.CurrentValue, peer.Send);
        if (request.ScopeDigest != scope.Digest()) throw new UnauthorizedAccessException("Bootstrap scope mismatch");
        var offerPath = files.PathFor("snapshot-offer", batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
        var manifest = files.Read<RuntimeStateBootstrapManifest>(offerPath);
        manifest?.Validate();
        var offer = manifest?.Header;
        var pages = new RuntimeStateBootstrapPages(options.CurrentValue.StateDirectory, "export-page", options.CurrentValue);
        if (manifest is not null && manifest.ExportPolicyDigest != RuntimeStateBootstrapImage.Hash(peer.Send)
            && (request.Phase != "fetch" || offer?.RequestId == request.RequestId)) return Conflict();
        if (request.Phase == "fetch")
        {
            if (offer?.RequestId != request.RequestId)
            {
                var fence = store.GetDurableSyncCheckpoint(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
                if ((state?.Epoch ?? fence?.Epoch ?? batch.Epoch) != batch.Epoch)
                    return Conflict();
                var image = store.CaptureBootstrap(batch.ClusterId, batch.SenderNodeId, request.RequestId, batch.Epoch, scope);
                manifest = pages.Capture(image, token) with { ExportPolicyDigest = RuntimeStateBootstrapImage.Hash(peer.Send) };
                files.Save(offerPath, manifest, token);
                pages.PruneOtherSnapshots(manifest);
                offer = manifest.Header;
            }
            if (UtcNow() - offer.CapturedAt > TimeSpan.FromMinutes(5)) return Conflict();
            if ((offer.OldEpoch != batch.Epoch && !(offer.NewEpoch == batch.Epoch && state?.Epoch == offer.NewEpoch))
                || offer.Scope.Digest() != scope.Digest())
                return Conflict();
            return new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence,
                batch.Digest, "SnapshotOffered", Manifest: manifest);
        }
        if (request.Phase == "page")
        {
            if (offer is null || request.RequestId != offer.RequestId || request.ImageDigest != offer.Digest
                || request.PageIndex is not { } index || index < 0 || index >= manifest!.PageDigests.Length
                || (batch.Epoch != offer.OldEpoch && batch.Epoch != offer.NewEpoch)
                || offer.Scope.Digest() != scope.Digest() || UtcNow() - offer.CapturedAt > TimeSpan.FromMinutes(5))
                return Conflict();
            var page = pages.Read(manifest, index) ?? throw new InvalidDataException("Missing durable bootstrap page");
            return new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence,
                batch.Digest, "SnapshotPage", Page: page);
        }
        if (offer is null || offer.RequestId != request.RequestId || offer.Digest != request.ImageDigest
            || offer.NewEpoch != batch.Epoch || offer.Scope.Digest() != scope.Digest())
            return Conflict();
        if (UtcNow() - offer.CapturedAt > TimeSpan.FromMinutes(5)) return Conflict();
        var committedFence = store.GetDurableSyncCheckpoint(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
        if (committedFence is not null && committedFence.Epoch != offer.OldEpoch && committedFence.Epoch != offer.NewEpoch)
            return Conflict();
        if (state is not null && state.Epoch != offer.OldEpoch && state.Epoch != offer.NewEpoch)
            return Conflict();
        if (state?.Epoch != offer.NewEpoch)
        {
            // The zero cursor is an internal durable fence, never an externally accepted ordinary batch.
            await store.MergeDurablyAsync(new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId,
                offer.NewEpoch, 0, [], offer.Digest), token);
            files.Save(path, new State(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId,
                offer.NewEpoch, 0, new(), UtcNow(), offer.Digest), token);
        }
        return new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, offer.NewEpoch, 0, offer.Digest, "Bootstrapped");
    }
}
