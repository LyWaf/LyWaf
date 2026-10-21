namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateSyncSender
{
    private sealed record CcReceipt(RuntimeStateMutation Mutation, string Endpoint, DateTimeOffset ConfirmedAt);

    private CcReceipt[] RetainCcConfirmations(IReadOnlyList<CcReceipt>? previous, RuntimeStateSyncBatch batch)
    {
        var now = DateTimeOffset.UtcNow;
        var endpoint = options.CurrentValue.Peers.TryGetValue(batch.ReceiverNodeId, out var peer) ? peer.Endpoint : "";
        var retained = (previous ?? []).Where(r => r.Mutation.ExpiresAt is null || r.Mutation.ExpiresAt > now)
            .ToDictionary(r => (r.Mutation.Domain, r.Mutation.Key));
        foreach (var mutation in batch.Mutations.Where(m => m.Domain is "cc.decision" or CcPolicyGeneration.Domain))
            retained[(mutation.Domain, mutation.Key)] = new(mutation, endpoint, now);
        var result = retained.Values.ToArray();
        // Never evict an active confirmation to make ordinary traffic fit. A full ledger leaves Pending intact.
        if (result.Length > 4096 || System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, RuntimeStateSyncProtocol.Json).Length > 1024 * 1024)
            throw new IOException("CC confirmation retention capacity exhausted");
        return result;
    }

    internal RuntimeStateMutation[] ReadCcConfirmations(string cluster, string sender, string receiver)
    {
        var current = options.CurrentValue;
        if (!current.Peers.TryGetValue(receiver, out var peer) || !peer.Enabled) return [];
        var files = new RuntimeStateSyncFiles(current.StateDirectory, current);
        var state = Read(files, files.PathFor("out", cluster, sender, receiver), cluster, sender, receiver);
        // The ledger and cursor commit atomically. Legacy unbound history cannot establish current confirmation.
        var now = DateTimeOffset.UtcNow;
        return state?.CcReceipts?.Where(r => r.Endpoint == peer.Endpoint
            && r.ConfirmedAt <= now && r.ConfirmedAt > now.AddSeconds(-Math.Max(1, peer.SyncOfflineWindowSeconds))
            && (r.Mutation.ExpiresAt is null || r.Mutation.ExpiresAt > now)).Select(r => r.Mutation).ToArray() ?? [];
    }
}
