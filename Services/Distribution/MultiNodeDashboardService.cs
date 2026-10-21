using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.Distribution;

/// <summary>并行采集节点快照、保留最后成功状态，并聚合安全计数和去重事件。</summary>
public sealed class MultiNodeDashboardService(
    IOptionsMonitor<DistributionOptions> options,
    IDistributionNodeClient nodeClient,
    TimeProvider? timeProvider = null,
    IOptionsMonitor<NodeCommunicationOptions>? communication = null,
    NodeDashboardDiagnostics? diagnostics = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ManagedNodeSnapshot> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (NodeDashboardSnapshot Snapshot, DateTimeOffset SeenAt)> _lastGood =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<MultiNodeDashboard> GetAsync(string username, CancellationToken cancellationToken)
    {
        EnsureController();
        var current = options.CurrentValue;
        var admissionConfig = communication?.CurrentValue;
        var generatedAt = _time.GetUtcNow();
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = current.Nodes.Where(x => x.Value.Enabled).Select(pair =>
        {
            var cached = _latest.TryGetValue(pair.Key, out var value) && value.Endpoint == pair.Value.Endpoint ? value
                : new ManagedNodeSnapshot(pair.Key, pair.Value.Endpoint, pair.Value.Tags, false, false, null, false,
                    null, "Awaiting node observation", false, null, "node_observation_pending");
            var expired = (cached.LastSeenAt ?? cached.FirstAttemptAt) is { } seen
                && generatedAt - seen >= TimeSpan.FromSeconds(Math.Clamp(current.OfflineTimeoutSeconds, 10, 3600));
            var admission = "Legacy";
            var admissionEnabled = true;
            if (admissionConfig is { Enabled: true })
            {
                var peer = admissionConfig.Peers.GetValueOrDefault(pair.Key);
                admission = peer is null ? "Unknown" : NodeAdmissionPolicy.IsValid(peer.AdmissionState) ? peer.AdmissionState : "Invalid";
                admissionEnabled = peer?.Enabled ?? false;
            }
            var online = cached.Online && !expired;
            // Admission is live configuration; observation errors/lifecycle remain untouched.
            return cached with { Tags = pair.Value.Tags, CanOperate = CanNode(username, pair.Key, current),
                Online = online, Stale = cached.Stale || expired,
                AdmissionState = admission, AdmissionEnabled = admissionEnabled,
                HealthyActive = online && !cached.Stale && !cached.ClockSkewWarning && cached.Lifecycle == "Active"
                    && admissionEnabled && admission is "Active" or "Legacy",
                Lifecycle = cached.Lifecycle == "AuthenticationFailed" ? cached.Lifecycle : expired ? "Offline" : cached.Lifecycle };
        }).OrderBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();
        var snapshots = nodes.Where(x => x.Online && x.Snapshot is not null).Select(x => x.Snapshot!).ToArray();
        var security = new NodeSecuritySnapshot(snapshots.Sum(x => x.Security.TotalInterceptCount),
            snapshots.Sum(x => x.Security.WafInterceptCount), snapshots.Sum(x => x.Security.BlacklistBlockCount),
            snapshots.Sum(x => x.Security.CcAttackCount), snapshots.Sum(x => x.Security.CrawlerDetectCount),
            snapshots.Sum(x => x.Security.GeoBlockCount), snapshots.Sum(x => x.Security.RateLimitCount),
            nodes.All(x => x.Online) && snapshots.All(x => x.AttackIps is { Count: <= 4096 } ips &&
                ips.Count == x.Security.UniqueAttackIps)
                ? snapshots.SelectMany(x => x.AttackIps!).Distinct(StringComparer.Ordinal).Count() : null);
        var events = snapshots.SelectMany(x => x.RecentEvents)
            .GroupBy(x => x.EventId, StringComparer.Ordinal).Select(x => x.OrderByDescending(e => e.LastHitAt).First())
            .OrderByDescending(x => x.LastHitAt).Take(200).ToArray();
        var missing = nodes.Where(x => !x.Online || x.Snapshot is null).Select(x => x.NodeId).ToArray();
        var coverage = new NodeAggregationCoverage(nodes.Length, snapshots.Length,
            nodes.Length > 0 && missing.Length == 0, missing,
            snapshots.Length == 0 ? null : snapshots.Min(x => x.Timestamp),
            snapshots.Length > 0 && snapshots.All(x => x.StatisticsStartedAt is not null));
        return Task.FromResult(new MultiNodeDashboard(generatedAt, CanGlobal(username, current), nodes, security, events, coverage,
            NodeTelemetryAggregation.Aggregate(nodes, generatedAt), nodes.Count(x => x.HealthyActive)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.HeartbeatIntervalSeconds, 1, 300)), _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !current.Role.Equals("Controller", StringComparison.OrdinalIgnoreCase))
        { _latest.Clear(); _lastGood.Clear(); return; }
        foreach (var id in _latest.Keys.Where(id => !current.Nodes.TryGetValue(id, out var node) || !node.Enabled))
        { _latest.TryRemove(id, out _); _lastGood.TryRemove(id, out _); }
        var diagnosticSnapshots = diagnostics is null ? null : await diagnostics.CapturePeersAsync(current, _time.GetUtcNow(), cancellationToken);
        await Parallel.ForEachAsync(current.Nodes.Where(x => x.Value.Enabled),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (pair, token) =>
            {
                if (_latest.TryGetValue(pair.Key, out var prior) && prior.Endpoint != pair.Value.Endpoint)
                    _lastGood.TryRemove(pair.Key, out _);
                var result = await CaptureAsync(pair.Key, pair.Value, "", _time.GetUtcNow(), token);
                _latest[pair.Key] = result with { Diagnostics = diagnosticSnapshots?.GetValueOrDefault(pair.Key) };
            });
    }

    public bool CanOperate(string username, IEnumerable<string> nodeIds)
    {
        var current = options.CurrentValue;
        return nodeIds.All(nodeId => CanNode(username, nodeId, current));
    }

    public bool CanOperateGlobally(string username) => CanGlobal(username, options.CurrentValue);

    private async Task<ManagedNodeSnapshot> CaptureAsync(string nodeId, DistributionNodeOptions node,
        string username, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await nodeClient.GetDashboardAsync(nodeId, cancellationToken);
            if (!string.Equals(snapshot.NodeId, nodeId, StringComparison.Ordinal))
                throw new InvalidDataException("Node response identity mismatch");
            var receivedAt = _time.GetUtcNow();
            _lastGood.TryGetValue(nodeId, out var priorGood);
            snapshot = snapshot with { Telemetry = NodeTelemetryAggregation.RetainCompleted(snapshot, priorGood.Snapshot, receivedAt),
                LogicalBans = NodeLogicalBanTelemetry.Retain(snapshot, priorGood.Snapshot, receivedAt) };
            _lastGood[nodeId] = (snapshot, receivedAt);
            // Use the request midpoint so ordinary network latency is not reported as clock drift.
            var requestMidpoint = requestedAt + TimeSpan.FromTicks((receivedAt - requestedAt).Ticks / 2);
            var skew = (snapshot.Timestamp - requestMidpoint).TotalSeconds;
            var threshold = Math.Clamp(options.CurrentValue.AllowedClockSkewSeconds, 5, 300);
            return new(nodeId, node.Endpoint, node.Tags, true, false, skew, Math.Abs(skew) > threshold,
                receivedAt, null, CanNode(username, nodeId, options.CurrentValue), snapshot,
                Lifecycle: "Active", FirstAttemptAt: requestedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or KeyNotFoundException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _lastGood.TryGetValue(nodeId, out var cached);
            var failure = NodeFailure.FromException(ex);
            var previous = _latest.TryGetValue(nodeId, out var observation) && observation.Endpoint == node.Endpoint ? observation : null;
            var failures = Math.Min((long)(previous?.ConsecutiveFailures ?? 0) + 1, int.MaxValue);
            var firstAttempt = previous?.FirstAttemptAt ?? requestedAt;
            var authenticationFailed = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            var offline = requestedAt - (cached.Snapshot is null ? firstAttempt : cached.SeenAt)
                >= TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.OfflineTimeoutSeconds, 10, 3600));
            var lifecycle = authenticationFailed ? "AuthenticationFailed" : offline ? "Offline"
                : failures >= Math.Clamp(options.CurrentValue.SuspectFailureThreshold, 1, 100) ? "Suspect"
                : cached.Snapshot is null ? "Connecting" : "Active";
            return new(nodeId, node.Endpoint, node.Tags, false, cached.Snapshot is not null, previous?.ClockSkewSeconds, previous?.ClockSkewWarning ?? false,
                cached.Snapshot is null ? null : cached.SeenAt, failure.Code,
                CanNode(username, nodeId, options.CurrentValue), cached.Snapshot, failure.Code, failure.RemoteTraceId,
                lifecycle, (int)failures, firstAttempt);
        }
    }

    private static bool CanGlobal(string username, DistributionOptions current)
    {
        var permissions = current.Permissions;
        if (permissions.GlobalOperators.Count == 0 && permissions.NodeOperators.Count == 0) return true;
        return permissions.GlobalOperators.Contains(username, StringComparer.OrdinalIgnoreCase);
    }

    private static bool CanNode(string username, string nodeId, DistributionOptions current)
        => CanGlobal(username, current) || current.Permissions.NodeOperators.TryGetValue(nodeId, out var users)
            && users.Contains(username, StringComparer.OrdinalIgnoreCase);

    private void EnsureController()
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !current.Role.Equals("Controller", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前实例不是多节点控制器");
    }
}
