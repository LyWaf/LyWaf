using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.Distribution;

// Counts are disjoint buckets, with a final overflow bucket (not cumulative buckets).
public sealed record NodeTelemetryWindow(string BootId, string Schema, DateTimeOffset Start,
    DateTimeOffset End, DateTimeOffset Watermark, bool Complete, long Requests, long Errors,
    long Intercepts, double[] BoundsMilliseconds, long[] Counts,
    IReadOnlyList<string>? VisitorHashes = null, bool VisitorsComplete = false,
    string VisitorMethod = "minute-client-ip-sha256-v1");

public sealed record NodeTelemetryAggregate(DateTimeOffset Start, DateTimeOffset End,
    string ExpectedSetVersion, string Status, IReadOnlyList<string> IncludedNodes,
    IReadOnlyDictionary<string, string> MissingNodes, DateTimeOffset? Watermark,
    long Requests, long Errors, long Intercepts, double? ErrorRate, double? InterceptRate,
    double? P95Milliseconds, double? P99Milliseconds, bool QuantilesApproximate,
    string QuantileMethod, double[] BoundsMilliseconds, long[] Counts,
    string Schema = NodeTelemetryCollector.Schema,
    NodeSetMetric? Visitors = null, NodeSetMetric? LogicalBans = null,
    IReadOnlyDictionary<string, string>? SourceGenerations = null);

/// <summary>Bounded local raw observations; independent of optional metrics exporters.</summary>
public sealed class NodeTelemetryCollector(TimeProvider? timeProvider = null)
{
    internal const string Schema = "proxy-completions-v1:5xx-or-exception:intercept-marker:ms";
    private static readonly double[] Bounds = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000];
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly DateTimeOffset _started = (timeProvider ?? TimeProvider.System).GetUtcNow();
    private readonly string _boot = Guid.NewGuid().ToString("N");
    internal string BootId => _boot;
    private readonly object _gate = new();
    private readonly Dictionary<long, Bucket> _buckets = new();
    private long _latestMinute = long.MinValue;
    public static DateTimeOffset Minute(DateTimeOffset time) => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds() / 60 * 60);

    public void Record(double milliseconds, int statusCode, bool intercepted, bool failed = false, string? clientIp = null)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            var minute = Minute(_time.GetUtcNow()).ToUnixTimeSeconds();
            Advance(minute);
            if (minute < _latestMinute - 180) return;
            if (!_buckets.TryGetValue(minute, out var bucket)) _buckets[minute] = bucket = new(Bounds.Length + 1);
            bucket.Requests++;
            if (failed || statusCode >= 500) bucket.Errors++;
            if (intercepted) bucket.Intercepts++;
            var index = Array.FindIndex(Bounds, bound => milliseconds <= bound);
            bucket.Counts[index < 0 ? Bounds.Length : index]++;
            if (!System.Net.IPAddress.TryParse(clientIp, out var address)) bucket.VisitorsComplete = false;
            else
            {
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{minute}:{address}")));
                if (bucket.Visitors.Count < 4096 || bucket.Visitors.Contains(hash)) bucket.Visitors.Add(hash);
                else bucket.VisitorsComplete = false;
            }
        }
    }

    public IReadOnlyList<NodeTelemetryWindow> Capture()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var minute = Minute(now).ToUnixTimeSeconds();
            Advance(minute);
            return Enumerable.Range(0, 4).Select(offset =>
            {
                var start = DateTimeOffset.FromUnixTimeSeconds(minute - offset * 60);
                var end = start.AddMinutes(1);
                _buckets.TryGetValue(start.ToUnixTimeSeconds(), out var bucket);
                return new NodeTelemetryWindow(_boot, Schema, start, end, end <= now ? end : now,
                    start >= _started && end <= now && minute >= _latestMinute,
                    bucket?.Requests ?? 0, bucket?.Errors ?? 0, bucket?.Intercepts ?? 0,
                    (double[])Bounds.Clone(), bucket is null ? new long[Bounds.Length + 1] : (long[])bucket.Counts.Clone(),
                    bucket?.Visitors.ToArray() ?? [], bucket?.VisitorsComplete ?? true);
            }).ToArray();
        }
    }

    private void Advance(long minute)
    {
        _latestMinute = Math.Max(_latestMinute, minute);
        foreach (var old in _buckets.Keys.Where(x => x < _latestMinute - 180).ToArray()) _buckets.Remove(old);
    }

    private sealed class Bucket(int length)
    {
        public long Requests, Errors, Intercepts;
        public long[] Counts = new long[length];
        public HashSet<string> Visitors = new(StringComparer.Ordinal);
        public bool VisitorsComplete = true;
    }
}

internal static class NodeTelemetryAggregation
{
    // Completed buckets are immutable. Retain already observed history across a node restart,
    // without adding repeated snapshots or mixing partial boot generations.
    internal static IReadOnlyList<NodeTelemetryWindow>? RetainCompleted(NodeDashboardSnapshot incoming,
        NodeDashboardSnapshot? previous, DateTimeOffset now)
    {
        if (previous?.Telemetry is null) return incoming.Telemetry;
        var earliest = NodeTelemetryCollector.Minute(now).AddMinutes(-3);
        var windows = (incoming.Telemetry ?? []).Where(x => x is not null && x.Start >= earliest && x.Start <= now).Take(4).ToList();
        foreach (var old in previous.Telemetry.Where(x => x is not null && x.Start >= earliest &&
            x.End <= now && x.Complete && x.Watermark == x.End && Valid(x)))
        {
            var matches = windows.Where(x => x.Start == old.Start && x.End == old.End).ToArray();
            if (matches.Length == 0) windows.Add(old);
            else if (matches.Length == 1 && !matches[0].Complete && matches[0].BootId != old.BootId)
            { windows.Remove(matches[0]); windows.Add(old); }
        }
        return windows.OrderByDescending(x => x.Start).Take(4).ToArray();
    }

    internal static NodeTelemetryAggregate Aggregate(IReadOnlyList<ManagedNodeSnapshot> nodes, DateTimeOffset now)
    {
        var end = NodeTelemetryCollector.Minute(now);
        var start = end.AddMinutes(-1);
        var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            nodes.OrderBy(x => x.NodeId, StringComparer.Ordinal).Select(x => new { x.NodeId, x.Endpoint }))));
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        var included = new List<string>();
        var visitorIncluded = new List<string>();
        var visitorMissing = new Dictionary<string, string>(StringComparer.Ordinal);
        var visitors = new HashSet<string>(StringComparer.Ordinal);
        var generations = new Dictionary<string, string>(StringComparer.Ordinal);
        double[]? bounds = null;
        long[]? counts = null;
        long requests = 0, errors = 0, intercepts = 0;
        DateTimeOffset? watermark = null;
        foreach (var node in nodes)
        {
            var candidates = node.Snapshot?.Telemetry?.Where(x => x is not null && x.Start == start && x.End == end).ToArray();
            string? reason = node.ClockSkewWarning ? "clock_skew" : candidates is null ? "telemetry_missing"
                : candidates.Length != 1 ? "window_missing_or_duplicate" : null;
            var window = candidates is { Length: 1 } ? candidates[0] : null;
            if (reason is null && window is not null)
            {
                reason = !window.Complete || window.Watermark < end ? "window_incomplete"
                    : window.Watermark != end || node.Snapshot!.Timestamp < end ? "invalid_watermark"
                    : !Valid(window) ? "invalid_histogram_or_counts"
                    : window.Schema != NodeTelemetryCollector.Schema || bounds is not null && !bounds.SequenceEqual(window.BoundsMilliseconds)
                        ? "incompatible_histogram" : null;
            }
            if (reason is not null || window is null)
            { missing[node.NodeId] = reason ?? "telemetry_missing"; visitorMissing[node.NodeId] = reason ?? "telemetry_missing"; continue; }
            bounds ??= (double[])window.BoundsMilliseconds.Clone();
            counts ??= new long[window.Counts.Length];
            // Check the whole addition before committing any part of this node.
            try
            {
                checked
                {
                    var nextRequests = requests + window.Requests;
                    var nextErrors = errors + window.Errors;
                    var nextIntercepts = intercepts + window.Intercepts;
                    var nextCounts = counts.Select((value, i) => checked(value + window.Counts[i])).ToArray();
                    requests = nextRequests; errors = nextErrors; intercepts = nextIntercepts; counts = nextCounts;
                }
            }
            catch (OverflowException) { missing[node.NodeId] = "count_overflow"; visitorMissing[node.NodeId] = "count_overflow"; continue; }
            included.Add(node.NodeId);
            generations[node.NodeId] = window.BootId;
            if (!window.VisitorsComplete || window.VisitorHashes is null) visitorMissing[node.NodeId] = "visitor_set_incomplete";
            else if (window.VisitorMethod != "minute-client-ip-sha256-v1") visitorMissing[node.NodeId] = "incompatible_method";
            else if (window.VisitorHashes.Count > 4096 || window.VisitorHashes.Count > window.Requests ||
                window.VisitorHashes.Any(hash => hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))) visitorMissing[node.NodeId] = "invalid_set";
            else { visitors.UnionWith(window.VisitorHashes); visitorIncluded.Add(node.NodeId); }
            watermark = watermark is null || window.Watermark < watermark ? window.Watermark : watermark;
        }
        bounds ??= []; counts ??= [];
        return new(start, end, version, nodes.Count > 0 && missing.Count == 0 ? "Complete" : "Partial",
            included, missing, watermark, requests, errors, intercepts,
            requests == 0 ? null : (double)errors / requests,
            requests == 0 ? null : (double)intercepts / requests,
            Quantile(.95), Quantile(.99), true, "nearest-rank bucket upper bound; overflow is null", bounds, counts,
            Visitors: new(nodes.Count > 0 && visitorMissing.Count == 0 ? "Complete" : "Partial",
                nodes.Count > 0 && visitorMissing.Count == 0 ? visitors.Count : null, visitors.Count,
                visitorIncluded, visitorMissing, "minute-client-ip-sha256-v1", true, watermark,
                visitorIncluded.ToDictionary(id => id, id => generations[id], StringComparer.Ordinal)),
            LogicalBans: NodeLogicalBanTelemetry.Aggregate(nodes, start, end), SourceGenerations: generations);

        double? Quantile(double quantile)
        {
            if (requests == 0) return null;
            var rank = Math.Ceiling(requests * quantile);
            long cumulative = 0;
            for (var i = 0; i < bounds.Length; i++)
            { cumulative += counts[i]; if (cumulative >= rank) return bounds[i]; }
            return null;
        }
    }

    private static bool Valid(NodeTelemetryWindow window)
    {
        if (!Guid.TryParseExact(window.BootId, "N", out _) || window.Requests < 0 || window.Errors < 0 ||
            window.Intercepts < 0 || window.Errors > window.Requests || window.Intercepts > window.Requests ||
            window.BoundsMilliseconds is not { Length: > 0 and <= 128 } ||
            window.Counts is null || window.Counts.Length != window.BoundsMilliseconds.Length + 1) return false;
        double previous = -1;
        foreach (var bound in window.BoundsMilliseconds)
        { if (!double.IsFinite(bound) || bound <= previous) return false; previous = bound; }
        try { return window.Counts.All(x => x >= 0) && window.Counts.Sum() == window.Requests; }
        catch (OverflowException) { return false; }
    }
}
