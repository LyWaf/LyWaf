namespace LyWaf.Services.Statistic;

public sealed record CcNodeDecisionEvidence(long ObservedCount, long InterceptedRequests, DateTimeOffset? FirstHit, DateTimeOffset? LastHit);
public sealed record CcBanEntry(long Id, DateTimeOffset Time, string Ip, string Reason, double DurationSeconds,
    string? DecisionId = null, string? ProtectionScope = null, string? RuleRevision = null,
    IReadOnlyDictionary<string, CcNodeDecisionEvidence>? Nodes = null, string PropagationStatus = "pending",
    LyWaf.Services.RuntimeState.CcPropagationStatus? Propagation = null,
    CcPolicyDescriptor? Policy = null, CcExecutionState? Execution = null, CcDecisionCoverage? Coverage = null,
    CcExecutionAggregate? ExecutionPropagation = null);

/// <summary>Bounded process-local CC ban history; independent of current ban expiration.</summary>
public sealed class CcBanLog
{
    public static CcBanLog Shared { get; } = new();
    private readonly object _gate = new();
    private readonly List<CcBanEntry> _entries = new();
    private long _sequence;

    public void Add(string ip, string reason, TimeSpan duration)
    {
        lock (_gate)
        {
            if (_entries.Count == 1000) _entries.RemoveAt(0);
            _entries.Add(new(++_sequence, DateTimeOffset.UtcNow, ip[..Math.Min(ip.Length, 128)],
                reason[..Math.Min(reason.Length, 1024)], duration.TotalSeconds));
        }
    }

    public void UpsertDecision(CcDecision decision, string ruleName)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.DecisionId == decision.DecisionId);
            if (index < 0 && _entries.Count == 1000 && decision.CreatedAt <= _entries[0].Time) return;
            var existing = index < 0 ? null : _entries[index];
            var nodes = existing?.Nodes?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
            nodes.TryGetValue(decision.OriginNode, out var previous);
            nodes[decision.OriginNode] = new(Math.Max(previous?.ObservedCount ?? 0, decision.ObservedCount),
                Math.Max(previous?.InterceptedRequests ?? 0, decision.InterceptedRequests),
                Min(previous?.FirstHit, decision.FirstHit), Max(previous?.LastHit, decision.LastHit));
            var time = existing is not null && existing.Time < decision.CreatedAt ? existing.Time : decision.CreatedAt;
            var entry = new CcBanEntry(existing?.Id ?? ++_sequence, time, decision.Policy.Subject,
                $"CC: {ruleName}", (decision.ExpiresAt - decision.CreatedAt).TotalSeconds, decision.DecisionId,
                decision.Policy.ProtectionScope, decision.Policy.RuleRevision, nodes, Policy: decision.Policy);
            if (index >= 0) _entries[index] = entry; else _entries.Add(entry);
            _entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (_entries.Count > 1000) _entries.RemoveAt(0);
        }
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;
    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    public CcBanEntry[] Read()
    {
        lock (_gate) return _entries.AsEnumerable().Reverse().ToArray();
    }
}
