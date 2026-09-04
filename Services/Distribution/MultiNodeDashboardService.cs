using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Distribution;

/// <summary>并行采集节点快照、保留最后成功状态，并聚合安全计数和去重事件。</summary>
public sealed class MultiNodeDashboardService(
    IOptionsMonitor<DistributionOptions> options,
    IDistributionNodeClient nodeClient)
{
    private readonly ConcurrentDictionary<string, (NodeDashboardSnapshot Snapshot, DateTimeOffset SeenAt)> _lastGood =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<MultiNodeDashboard> GetAsync(string username, CancellationToken cancellationToken)
    {
        EnsureController();
        var current = options.CurrentValue;
        var generatedAt = DateTimeOffset.UtcNow;
        var tasks = current.Nodes.Where(x => x.Value.Enabled)
            .Select(pair => CaptureAsync(pair.Key, pair.Value, username, generatedAt, cancellationToken));
        var nodes = (await Task.WhenAll(tasks)).OrderBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();
        var snapshots = nodes.Where(x => x.Online && x.Snapshot is not null).Select(x => x.Snapshot!).ToArray();
        var security = new NodeSecuritySnapshot(snapshots.Sum(x => x.Security.TotalInterceptCount),
            snapshots.Sum(x => x.Security.WafInterceptCount), snapshots.Sum(x => x.Security.BlacklistBlockCount),
            snapshots.Sum(x => x.Security.CcAttackCount), snapshots.Sum(x => x.Security.CrawlerDetectCount),
            snapshots.Sum(x => x.Security.GeoBlockCount), snapshots.Sum(x => x.Security.RateLimitCount),
            snapshots.Sum(x => x.Security.UniqueAttackIps));
        var events = snapshots.SelectMany(x => x.RecentEvents)
            .GroupBy(x => x.EventId, StringComparer.Ordinal).Select(x => x.OrderByDescending(e => e.LastHitAt).First())
            .OrderByDescending(x => x.LastHitAt).Take(200).ToArray();
        return new(generatedAt, CanGlobal(username, current), nodes, security, events);
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
            var receivedAt = DateTimeOffset.UtcNow;
            _lastGood[nodeId] = (snapshot, receivedAt);
            // Use the request midpoint so ordinary network latency is not reported as clock drift.
            var requestMidpoint = requestedAt + TimeSpan.FromTicks((receivedAt - requestedAt).Ticks / 2);
            var skew = (snapshot.Timestamp - requestMidpoint).TotalSeconds;
            var threshold = Math.Clamp(options.CurrentValue.AllowedClockSkewSeconds, 5, 300);
            return new(nodeId, node.Endpoint, node.Tags, true, false, skew, Math.Abs(skew) > threshold,
                receivedAt, null, CanNode(username, nodeId, options.CurrentValue), snapshot);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or KeyNotFoundException)
        {
            _lastGood.TryGetValue(nodeId, out var cached);
            return new(nodeId, node.Endpoint, node.Tags, false, cached.Snapshot is not null, null, false,
                cached.Snapshot is null ? null : cached.SeenAt, ex.Message,
                CanNode(username, nodeId, options.CurrentValue), cached.Snapshot);
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
