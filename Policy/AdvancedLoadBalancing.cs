using System.Collections.Concurrent;
using System.Globalization;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Policy;

/// <summary>
/// 组合 Priority/Failover、Zone/Region、Subset 与慢启动的负载均衡策略。
/// 输入集合已由 YARP 主动/被动健康策略过滤，本策略不会重新引入不可用实例。
/// </summary>
public sealed class AdvancedLoadBalancingPolicy : ILoadBalancingPolicy
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);
    public string Name => "Advanced";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster,
        IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0) return null;
        var clusterMetadata = cluster.Model.Config.Metadata;
        var candidates = FilterCandidates(context, availableDestinations, clusterMetadata);
        if (candidates.Count == 0) return null;
        var now = DateTimeOffset.UtcNow;
        return candidates.OrderBy(destination => Score(cluster.ClusterId, destination, clusterMetadata, now))
            .ThenBy(static destination => destination.DestinationId, StringComparer.Ordinal).First();
    }

    internal static IReadOnlyList<T> FilterCandidates<T>(HttpContext context, IReadOnlyList<T> candidates,
        IReadOnlyDictionary<string, string>? clusterMetadata, Func<T, IReadOnlyDictionary<string, string>?> metadata)
    {
        if (candidates.Count == 0) return candidates;
        var priority = candidates.Min(item => ReadInt(metadata(item), "Priority", 0));
        var selected = candidates.Where(item => ReadInt(metadata(item), "Priority", 0) == priority).ToArray();
        selected = PreferHeaderMatch(context, selected, clusterMetadata, metadata, "AdvancedLb.RegionHeader", "Region");
        selected = PreferHeaderMatch(context, selected, clusterMetadata, metadata, "AdvancedLb.ZoneHeader", "Zone");

        var subsetHeader = Read(clusterMetadata, "AdvancedLb.SubsetHeader");
        var subsetKey = Read(clusterMetadata, "AdvancedLb.SubsetKey") ?? "Subset";
        if (!string.IsNullOrWhiteSpace(subsetHeader) && context.Request.Headers.TryGetValue(subsetHeader, out var requested))
        {
            var subset = selected.Where(item => string.Equals(Read(metadata(item), subsetKey), requested.ToString(),
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (subset.Length > 0) selected = subset;
            else if (string.Equals(Read(clusterMetadata, "AdvancedLb.SubsetFallback"), "None", StringComparison.OrdinalIgnoreCase))
                return [];
        }
        return selected;
    }

    private static IReadOnlyList<DestinationState> FilterCandidates(HttpContext context,
        IReadOnlyList<DestinationState> candidates, IReadOnlyDictionary<string, string>? clusterMetadata) =>
        FilterCandidates(context, candidates, clusterMetadata, static item => item.Model.Config.Metadata);

    private double Score(string clusterId, DestinationState destination,
        IReadOnlyDictionary<string, string>? clusterMetadata, DateTimeOffset now)
    {
        var metadata = destination.Model.Config.Metadata;
        var weight = Math.Clamp(ReadInt(metadata, "Weight", 1), 1, 10_000);
        var slowStartSeconds = Math.Clamp(ReadInt(metadata, "SlowStartSeconds",
            ReadInt(clusterMetadata, "AdvancedLb.SlowStartSeconds", 0)), 0, 3600);
        if (slowStartSeconds > 0)
        {
            var firstSeen = _firstSeen.GetOrAdd(clusterId + "\n" + destination.DestinationId, now);
            weight = CalculateEffectiveWeight(weight, slowStartSeconds, now - firstSeen);
        }
        return (destination.ConcurrentRequestCount + 1d) / weight;
    }

    private static T[] PreferHeaderMatch<T>(HttpContext context, T[] candidates,
        IReadOnlyDictionary<string, string>? clusterMetadata,
        Func<T, IReadOnlyDictionary<string, string>?> metadata, string headerSetting, string destinationKey)
    {
        var header = Read(clusterMetadata, headerSetting);
        if (string.IsNullOrWhiteSpace(header) || !context.Request.Headers.TryGetValue(header, out var requested)) return candidates;
        var matched = candidates.Where(item => string.Equals(Read(metadata(item), destinationKey), requested.ToString(),
            StringComparison.OrdinalIgnoreCase)).ToArray();
        return matched.Length > 0 ? matched : candidates;
    }

    private static string? Read(IReadOnlyDictionary<string, string>? metadata, string key) =>
        metadata?.TryGetValue(key, out var value) == true ? value : null;
    private static int ReadInt(IReadOnlyDictionary<string, string>? metadata, string key, int fallback) =>
        int.TryParse(Read(metadata, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    internal static int CalculateEffectiveWeight(int weight, int slowStartSeconds, TimeSpan elapsed)
    {
        if (slowStartSeconds <= 0) return Math.Max(1, weight);
        var progress = Math.Clamp(elapsed.TotalSeconds / slowStartSeconds, 0.05, 1);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(1, weight) * progress));
    }
}
