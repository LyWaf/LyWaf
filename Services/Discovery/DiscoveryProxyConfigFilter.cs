using Yarp.ReverseProxy.Configuration;

namespace LyWaf.Services.Discovery;

/// <summary>在 YARP 构建 Cluster 快照时，用服务发现快照替换该 Cluster 的静态 Destinations。</summary>
public sealed class DiscoveryProxyConfigFilter(DiscoveryRegistry registry) : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        var snapshot = registry.Get(cluster.ClusterId);
        if (snapshot == null) return ValueTask.FromResult(cluster);
        var destinations = snapshot.Destinations.ToDictionary(
            x => x.Id,
            x => new DestinationConfig { Address = x.Address, Metadata = x.Metadata },
            StringComparer.OrdinalIgnoreCase);
        return ValueTask.FromResult(cluster with { Destinations = destinations });
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);
}
