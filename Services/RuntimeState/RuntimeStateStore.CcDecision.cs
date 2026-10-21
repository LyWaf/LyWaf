namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    // Audit projection may read already accepted old-generation evidence; enforcement must still check the fence.
    internal IReadOnlyList<RuntimeStateMutation> ReadCcDecisionHistory(string? stateKey = null)
        => _entries.Values.Where(m => m.Domain == "cc.decision" && !m.Deleted && m.Value is not null
            && (stateKey is null || m.Key.StartsWith(stateKey + "|", StringComparison.Ordinal))
            && (!m.ExpiresAt.HasValue || m.ExpiresAt > DateTimeOffset.UtcNow)).ToArray();
}
