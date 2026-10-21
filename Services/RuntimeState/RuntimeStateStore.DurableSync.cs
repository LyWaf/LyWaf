using System.Collections.Concurrent;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    private const string SyncCheckpointDomain = "runtime.sync.checkpoint";
    private readonly ConcurrentDictionary<string, RuntimeStateSyncAck> _syncCheckpoints = new(StringComparer.Ordinal);
    private bool _syncJournalCorrupt;
    private int _journalStopping;
    internal void EnsureSyncWriterAvailable()
    {
        if (Volatile.Read(ref _journalStopping) != 0) throw new IOException("Runtime journal is stopping");
    }
    internal bool SyncJournalHealthy => !_syncJournalCorrupt && !_journalCapacityFailed;
    private readonly ConcurrentDictionary<RuntimeStateMutation, TaskCompletionSource> _durableWrites =
        new(ReferenceEqualityComparer.Instance);
    internal Action? JournalBeforeDurableFlush { get; set; }

    internal static string SyncStreamKey(string cluster, string sender, string receiver) =>
        JsonSerializer.Serialize(new[] { cluster, sender, receiver });

    internal RuntimeStateSyncAck? GetDurableSyncCheckpoint(string cluster, string sender, string receiver) =>
        _syncCheckpoints.GetValueOrDefault(SyncStreamKey(cluster, sender, receiver));

    internal async Task MergeDurablyAsync(RuntimeStateSyncBatch batch, CancellationToken token,
        IReadOnlyList<RuntimeStateMutation>? appliedMutations = null)
    {
        EnsureSyncWriterAvailable();
        var mutations = (appliedMutations ?? batch.Mutations).Select(SnapshotMutation).ToArray();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        // Validate the complete batch before any side effects. Merge still validates combined counter bounds.
        foreach (var mutation in mutations)
        {
            ValidateMutation(mutation);
            if (mutation.Domain == CcPolicyGeneration.Domain) CcReleaseAuthorization.Verify(mutation, _options.CurrentValue);
        }
        foreach (var mutation in mutations)
        {
            token.ThrowIfCancellationRequested();
            if (Merge(mutation)) changed.Add(Identity(mutation.Domain, mutation.Key));
            Interlocked.Increment(ref _queueDepth);
            try { await QueueDurableAsync(mutation with { }, token); }
            catch { Interlocked.Decrement(ref _queueDepth); throw; }
        }
        var ack = new RuntimeStateSyncAck(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId,
            batch.Epoch, batch.Sequence, batch.Digest);
        ChargeMemory("sync:" + SyncStreamKey(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId), ack, reservation: true);
        var durable = new RuntimeStateMutation(SyncCheckpointDomain,
            SyncStreamKey(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId), NodeId, 0,
            DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(ack, JsonOptions));
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _durableWrites[durable] = completion;
            var queued = false;
            Interlocked.Increment(ref _queueDepth);
            try
            {
                await QueueDurableAsync(durable, token);
                queued = true;
                await completion.Task.WaitAsync(token);
                _syncCheckpoints[durable.Key] = ack;
                // Only a durable controller commit may fan out to other nodes. Unchanged uploads do not echo forever.
                if (_options.CurrentValue.ManagedTopologyRole == "Controller")
                    foreach (var key in changed)
                        if (_entries.TryGetValue(key, out var committed)) MutationPublished?.Invoke(committed);
            }
            finally
            {
                if (!queued) Interlocked.Decrement(ref _queueDepth);
                _durableWrites.TryRemove(durable, out _);
            }
        }
    }

    private RuntimeStateSyncAck? ReadSyncCheckpoint(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.TryGetProperty("domain", out var domain) || domain.GetString() != SyncCheckpointDomain) return null;
        if (!document.RootElement.TryGetProperty("value", out var value)) throw new JsonException("Missing sync checkpoint value");
        var ack = value.Deserialize<RuntimeStateSyncAck>(JsonOptions)
            ?? throw new JsonException("Invalid sync checkpoint");
        var record = document.RootElement.Deserialize<RuntimeStateMutation>(JsonOptions)
            ?? throw new JsonException("Invalid sync checkpoint record");
        if (!RuntimeStateSyncProtocol.ValidIdentity(ack.ClusterId) || !RuntimeStateSyncProtocol.ValidIdentity(ack.SenderNodeId)
            || !RuntimeStateSyncProtocol.ValidIdentity(ack.ReceiverNodeId) || !Guid.TryParseExact(ack.Epoch, "N", out _)
            || ack.Sequence < 0 || ack.Digest is not { Length: 64 } || !ack.Digest.All(char.IsAsciiHexDigit)
            || ack.Status != "Applied" || ack.ExpectedSequence != 0 || ack.ReceiverNodeId != NodeId
            || record.NodeId != NodeId || record.Sequence != 0 || record.Deleted || record.ExpiresAt is not null
            || record.CounterComponents is not null || record.CcReleaseProof is not null
            || record.Key != SyncStreamKey(ack.ClusterId, ack.SenderNodeId, ack.ReceiverNodeId))
            throw new JsonException("Invalid sync checkpoint");
        return ack;
    }

    private void RecoverSyncCheckpoint(RuntimeStateSyncAck ack)
    {
        var key = SyncStreamKey(ack.ClusterId, ack.SenderNodeId, ack.ReceiverNodeId);
        ChargeMemory("sync:" + key, ack);
        _syncCheckpoints[key] = ack;
    }

    private static string SerializeSyncCheckpoint(RuntimeStateSyncAck ack) => JsonSerializer.Serialize(
        new RuntimeStateMutation(SyncCheckpointDomain, SyncStreamKey(ack.ClusterId, ack.SenderNodeId, ack.ReceiverNodeId),
            ack.ReceiverNodeId, 0, DateTimeOffset.UtcNow, null, false,
            JsonSerializer.SerializeToElement(ack, JsonOptions)), JsonOptions);

    private async Task CompleteDurableWriteAsync(RuntimeStateMutation mutation, StreamWriter? writer)
    {
        if (mutation.Domain is not (SyncCheckpointDomain or BootstrapSnapshotDomain)) return;
        // Reserved checkpoints are never retired by compaction. No writer means no proven fence.
        if (writer is null) throw new IOException("Runtime sync checkpoint has no durable journal writer");
        _durableWrites.TryGetValue(mutation, out var completion);
        // Cancellation may stop the waiter, never convert a buffered write into a durable ACK.
        JournalBeforeDurableFlush?.Invoke();
        await writer.FlushAsync();
        ((FileStream)writer.BaseStream).Flush(flushToDisk: true);
        if (mutation.Domain == SyncCheckpointDomain)
            RecoverSyncCheckpoint(mutation.Value!.Value.Deserialize<RuntimeStateSyncAck>(JsonOptions)!);
        else
        {
            var image = mutation.Value!.Value.Deserialize<RuntimeStateBootstrapImage>(JsonOptions)!;
            foreach (var key in _journalExpiredVersions.Where(p => image.Scope.Contains(p.Value)).Select(p => p.Key).ToArray())
                _journalExpiredVersions.Remove(key);
        }
        completion?.TrySetResult();
    }

    private void FailDurableWrites(Exception error)
    {
        foreach (var completion in _durableWrites.Values) completion.TrySetException(error);
    }
}
