using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

/// <summary>One durable outstanding batch per directional stream, with a restart-stable epoch and ACK cursor.</summary>
public sealed partial class RuntimeStateSyncSender(IOptionsMonitor<RuntimeStateOptions> options)
{
    private sealed record State(string ClusterId, string SenderNodeId, string ReceiverNodeId,
        string Epoch, long AcknowledgedSequence, string LastDigest, RuntimeStateSyncBatch? Pending,
        IReadOnlyList<RuntimeStateSyncBatch>? History = null,
        IReadOnlyList<CcReceipt>? CcReceipts = null, string? BootstrapDigest = null);

    internal IReadOnlyList<RuntimeStateSyncBatch> ReadRange(RuntimeStateSyncBatch pending, long first)
    {
        if (first <= 0 || first >= pending.Sequence) throw new InvalidDataException("Invalid runtime sync gap");
        var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory, options.CurrentValue);
        var path = files.PathFor("out", pending.ClusterId, pending.SenderNodeId, pending.ReceiverNodeId);
        var state = Read(files, path, pending.ClusterId, pending.SenderNodeId, pending.ReceiverNodeId)
            ?? throw new InvalidDataException("Missing runtime sync sender history");
        if (state.Epoch != pending.Epoch || state.Pending?.Digest != pending.Digest)
            throw new InvalidDataException("Runtime sync gap does not match pending stream");
        var range = (state.History ?? []).Where(x => x.Sequence >= first).ToArray();
        if (range.Length == 0 || range[0].Sequence != first || range[^1].Sequence != pending.Sequence - 1
            || range.Where((entry, index) => entry.Sequence != first + index).Any())
            throw new RuntimeStateSyncHistoryUnavailableException();
        return range;
    }

    public RuntimeStateSyncBatch? ReadPending(string cluster, string sender, string receiver)
    {
        var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory, options.CurrentValue);
        var path = files.PathFor("out", cluster, sender, receiver);
        return Read(files, path, cluster, sender, receiver)?.Pending;
    }

    internal void Rebase(RuntimeStateSyncBatch oldPending, RuntimeStateBootstrapImage image)
    {
        image.Validate();
        var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory, options.CurrentValue);
        var path = files.PathFor("out", oldPending.ClusterId, oldPending.SenderNodeId, oldPending.ReceiverNodeId);
        using var held = files.Lock(path);
        var state = Read(files, path, oldPending.ClusterId, oldPending.SenderNodeId, oldPending.ReceiverNodeId, lockHeld: true)
            ?? throw new InvalidDataException("Missing bootstrap sender cursor");
        if (image.ClusterId != oldPending.ClusterId || image.RecipientNodeId != oldPending.SenderNodeId || image.AuthorityNodeId != oldPending.ReceiverNodeId)
            throw new InvalidDataException("Bootstrap sender identity mismatch");
        if (state.Epoch == image.NewEpoch) return;
        if (state.Pending?.Digest != oldPending.Digest || state.Epoch != oldPending.Epoch)
            throw new InvalidDataException("Bootstrap pending changed during installation");
        files.Save(path, new State(state.ClusterId, state.SenderNodeId, state.ReceiverNodeId, image.NewEpoch, 0, image.Digest, null, [], BootstrapDigest: image.Digest));
    }

    internal bool HasCompletedBootstrap(RuntimeStateBootstrapManifest manifest)
    {
        manifest.Validate();
        var image = manifest.Header;
        var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory, options.CurrentValue);
        var path = files.PathFor("out", image.ClusterId, image.RecipientNodeId, image.AuthorityNodeId);
        var state = Read(files, path, image.ClusterId, image.RecipientNodeId, image.AuthorityNodeId);
        // LastDigest supports already-rebased cursors written before BootstrapDigest was introduced.
        return state?.Epoch == image.NewEpoch && (state.BootstrapDigest == image.Digest
            || state.AcknowledgedSequence == 0 && state.LastDigest == image.Digest);
    }

    public RuntimeStateSyncBatch Prepare(string cluster, string sender, string receiver, IReadOnlyList<RuntimeStateMutation> mutations)
    {
        var current = options.CurrentValue;
        if (current.NodeId != sender || mutations.Any(m => RuntimeStateReplicationAuthorization.AuthorizeSend(current, receiver, m) is not null))
            throw new InvalidOperationException("Runtime sync send is not authorized");
        var files = new RuntimeStateSyncFiles(current.StateDirectory, current);
        var path = files.PathFor("out", cluster, sender, receiver);
        using var held = files.Lock(path);
        var state = Read(files, path, cluster, sender, receiver, lockHeld: true)
            ?? new State(cluster, sender, receiver, Guid.NewGuid().ToString("N"), 0, "", null);
        if (state.Pending is not null) throw new InvalidOperationException("An earlier runtime sync batch still requires confirmation");
        // Snapshot the payload before hashing/persisting; callers may subsequently coalesce their queued entries.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<RuntimeStateMutation[]>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(mutations, RuntimeStateSyncProtocol.Json), RuntimeStateSyncProtocol.Json)!;
        var batch = new RuntimeStateSyncBatch(cluster, sender, receiver, state.Epoch, checked(state.AcknowledgedSequence + 1), snapshot, "");
        batch = batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        RuntimeStateSyncProtocol.Validate(batch);
        files.Save(path, state with { Pending = batch });
        return batch;
    }

    public void Confirm(RuntimeStateSyncBatch batch, RuntimeStateSyncAck ack)
    {
        RuntimeStateSyncProtocol.Validate(batch);
        if (ack.Status != "Applied" || ack.ClusterId != batch.ClusterId || ack.SenderNodeId != batch.SenderNodeId
            || ack.ReceiverNodeId != batch.ReceiverNodeId || ack.Epoch != batch.Epoch || ack.Sequence != batch.Sequence
            || ack.Digest != batch.Digest) throw new InvalidDataException("Runtime sync ACK identity or digest mismatch");
        var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory, options.CurrentValue);
        var path = files.PathFor("out", batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId);
        using var held = files.Lock(path);
        var state = Read(files, path, batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, lockHeld: true)
            ?? throw new InvalidDataException("Runtime sync ACK has no durable sender state");
        if (state.Epoch != batch.Epoch) throw new InvalidDataException("Runtime sync ACK epoch mismatch");
        if (state.Pending is null && state.AcknowledgedSequence == batch.Sequence && state.LastDigest == batch.Digest) return;
        if (state.Pending?.Digest != batch.Digest || state.Pending.Sequence != batch.Sequence)
            throw new InvalidDataException("Runtime sync ACK does not match pending batch");
        var history = (state.History ?? []).Append(batch).TakeLast(64).ToList();
        while (history.Count > 0 && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(history, RuntimeStateSyncProtocol.Json).Length > 4 * 1024 * 1024)
            history.RemoveAt(0);
        var receipts = RetainCcConfirmations(state.CcReceipts, batch);
        files.Save(path, state with { AcknowledgedSequence = batch.Sequence, LastDigest = batch.Digest, Pending = null,
            History = history, CcReceipts = receipts });
    }

    private static State? Read(RuntimeStateSyncFiles files, string path, string cluster, string sender, string receiver, bool lockHeld = false)
    {
        if (!File.Exists(path))
        {
            if (lockHeld) return null;
            // Wait only while absent: a continuous writer must not starve observation after publishing a checkpoint.
            using var held = files.LockWhileMissing(path);
            if (held is not null) return Read(files, path, cluster, sender, receiver, lockHeld: true);
        }
        // Normal observation does not lock out ACK commit; once seen, disappearance is an error, not no pending.
        var state = files.Read<State>(path, requireExisting: true);
        if (state is null) return null;
        if (state.ClusterId != cluster || state.SenderNodeId != sender || state.ReceiverNodeId != receiver
            || !Guid.TryParseExact(state.Epoch, "N", out _) || state.AcknowledgedSequence < 0)
            throw new InvalidDataException("Invalid runtime sync sender checkpoint");
        if (state.Pending is { } pending)
        {
            RuntimeStateSyncProtocol.Validate(pending);
            if (pending.ClusterId != cluster || pending.SenderNodeId != sender || pending.ReceiverNodeId != receiver
                || pending.Epoch != state.Epoch || pending.Sequence != state.AcknowledgedSequence + 1)
                throw new InvalidDataException("Invalid runtime sync pending sequence");
        }
        if (state.History is { } history)
        {
            if (history.Count > 64) throw new InvalidDataException("Runtime sync history exceeds count limit");
            long previous = history.Count == 0 ? state.AcknowledgedSequence : history[0].Sequence - 1;
            foreach (var entry in history)
            {
                RuntimeStateSyncProtocol.Validate(entry);
                if (entry.ClusterId != cluster || entry.SenderNodeId != sender || entry.ReceiverNodeId != receiver
                    || entry.Epoch != state.Epoch || entry.Sequence != ++previous)
                    throw new InvalidDataException("Invalid runtime sync retained range");
            }
            if (previous != state.AcknowledgedSequence) throw new InvalidDataException("Runtime sync retained range does not end at ACK cursor");
        }
        return state;
    }
}

internal sealed class RuntimeStateSyncHistoryUnavailableException() : IOException("Runtime sync gap precedes retained history; snapshot bootstrap is required");
