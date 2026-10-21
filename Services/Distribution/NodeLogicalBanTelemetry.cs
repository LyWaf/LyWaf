using LyWaf.Services.Statistic;

namespace LyWaf.Services.Distribution;

public sealed record NodeLogicalBanWindow(string BootId, DateTimeOffset Start, DateTimeOffset End,
    DateTimeOffset ObservedAt, bool Complete, IReadOnlyList<string> DecisionIds, string? MissingReason,
    string Method = "observed-decision-id-union-by-created-at-v1");

public sealed record NodeSetMetric(string Status, long? Count, long ObservedCount,
    IReadOnlyList<string> IncludedNodes, IReadOnlyDictionary<string, string> MissingNodes,
    string Method, bool Final = false, DateTimeOffset? ObservedAt = null,
    IReadOnlyDictionary<string, string>? SourceGenerations = null);

/// <summary>Read-only bounded log projection. Coverage is not a promise that asynchronous decisions are final.</summary>
public sealed class NodeLogicalBanTelemetry(CcBanLog? log = null, TimeProvider? timeProvider = null) : IHostedService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly DateTimeOffset _started = (timeProvider ?? TimeProvider.System).GetUtcNow();
    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;

    public IReadOnlyList<NodeLogicalBanWindow> Capture(string boot)
    {
        var now = _clock.GetUtcNow();
        var rows = (log ?? CcBanLog.Shared).Read();
        var current = NodeTelemetryCollector.Minute(now);
        return Enumerable.Range(0, 4).Select(offset =>
        {
            var start = current.AddMinutes(-offset);
            var end = start.AddMinutes(1);
            var relevant = rows.Where(r => r.Time >= start && r.Time < end).ToArray();
            var reason = start < _started || end > now ? "window_incomplete"
                : rows.Length >= 1000 && rows.Min(r => r.Time) >= start ? "log_retention_gap"
                : relevant.Any(r => string.IsNullOrEmpty(r.DecisionId)) ? "legacy_decision_id_missing" : null;
            return new NodeLogicalBanWindow(boot, start, end, now, reason is null,
                relevant.Where(r => !string.IsNullOrEmpty(r.DecisionId)).Select(r => r.DecisionId!).Distinct(StringComparer.Ordinal).ToArray(), reason);
        }).ToArray();
    }

    internal static NodeSetMetric Aggregate(IReadOnlyList<ManagedNodeSnapshot> nodes, DateTimeOffset start, DateTimeOffset end)
    {
        var included = new List<string>();
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? observedAt = null;
        var generations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var matches = node.Snapshot?.LogicalBans?.Where(w => w is not null && w.Start == start && w.End == end).ToArray();
            var window = matches is { Length: 1 } ? matches[0] : null;
            var reason = node.ClockSkewWarning ? "clock_skew" : window is null ? "telemetry_missing"
                : !window.Complete ? window.MissingReason ?? "window_incomplete"
                : window.Method != "observed-decision-id-union-by-created-at-v1" ? "incompatible_method"
                : window.ObservedAt < end || !Guid.TryParseExact(window.BootId, "N", out _)
                    || window.DecisionIds is null || window.DecisionIds.Count > 1000
                    || window.DecisionIds.Any(id => id is not { Length: 64 } || !id.All(Uri.IsHexDigit)) ? "invalid_set" : null;
            if (reason is not null) { missing[node.NodeId] = reason; continue; }
            ids.UnionWith(window!.DecisionIds!);
            included.Add(node.NodeId);
            generations[node.NodeId] = window.BootId;
            observedAt = observedAt is null || window.ObservedAt < observedAt ? window.ObservedAt : observedAt;
        }
        var complete = nodes.Count > 0 && missing.Count == 0;
        return new(complete ? "Complete" : "Partial", complete ? ids.Count : null, ids.Count,
            included, missing, "observed-decision-id-union-by-created-at-v1", false, observedAt, generations);
    }

    internal static IReadOnlyList<NodeLogicalBanWindow>? Retain(NodeDashboardSnapshot incoming, NodeDashboardSnapshot? previous,
        DateTimeOffset now)
    {
        if (previous?.LogicalBans is null) return incoming.LogicalBans;
        var first = NodeTelemetryCollector.Minute(now).AddMinutes(-3);
        var result = (incoming.LogicalBans ?? []).Where(x => x is not null && x.Start >= first).Take(4).ToList();
        foreach (var old in previous.LogicalBans.Where(x => x is not null && x.Start >= first && x.Complete))
        {
            var next = result.Where(x => x.Start == old.Start && x.End == old.End).ToArray();
            if (next.Length == 0) result.Add(old);
            else if (next.Length == 1 && next[0].BootId != old.BootId && !next[0].Complete)
            { result.Remove(next[0]); result.Add(old); }
        }
        return result.OrderByDescending(x => x.Start).Take(4).ToArray();
    }
}
