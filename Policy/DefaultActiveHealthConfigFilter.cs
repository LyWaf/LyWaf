using Yarp.ReverseProxy.Configuration;

namespace LyWaf.Policy;

/// <summary>Supplies active upstream probe defaults while preserving explicit cluster settings.</summary>
public sealed class DefaultActiveHealthConfigFilter : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        var health = cluster.HealthCheck ?? new HealthCheckConfig();
        var active = health.Active ?? new ActiveHealthCheckConfig();
        // This reserved cluster routes local responses and has no real upstream.
        // Handle older generated configurations that lack the explicit disable flag.
        if (string.Equals(cluster.ClusterId, "cluster_unuse", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(cluster with
            {
                HealthCheck = health with { Active = active with { Enabled = false } }
            });
        if (active.Enabled == false)
            return ValueTask.FromResult(cluster);

        return ValueTask.FromResult(cluster with
        {
            HealthCheck = health with
            {
                Active = active with
                {
                    Enabled = true,
                    Policy = active.Policy ?? "LyxActiveHealth",
                    Path = active.Path ?? "/",
                    Interval = active.Interval ?? TimeSpan.FromSeconds(30),
                    Timeout = active.Timeout ?? TimeSpan.FromSeconds(5)
                }
            }
        });
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel)
        => ValueTask.FromResult(route);
}
