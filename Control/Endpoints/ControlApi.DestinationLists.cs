using LyWaf.Policy;
using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record ClusterChoice(string Id, string LoadBalancingPolicy, int DestinationCount, bool HasPatch);
    private sealed record DestinationPageItem(string Id, string Address, string Host, int Port, string Scheme,
        Dictionary<string, string> Metadata, string Source, OutlierSnapshot? Outlier);

    private static void MapDestinationListEndpoints(WebApplication app, int controlPort)
    {
        app.MapGet("/api/lb/cluster-options", (HttpContext ctx, IConfiguration config) =>
        {
            var choices = config.GetSection("ReverseProxy:Clusters").GetChildren().Select(cluster =>
            {
                var destinations = cluster.GetSection("Destinations").GetChildren().ToArray();
                return new ClusterChoice(cluster.Key, cluster["LoadBalancingPolicy"] ?? "RoundRobin", destinations.Length,
                    destinations.Any(destination => DetectConfigSource(config,
                        $"ReverseProxy:Clusters:{cluster.Key}:Destinations:{destination.Key}") == ConfigSource.PatchConfig));
            });
            return TypedResults.Ok(ManagementLists.From(choices).Sort("id", item => item.Id)
                .Sort("destinationCount", item => item.DestinationCount).Search(item => item.Id + " " + item.LoadBalancingPolicy)
                .Read(ctx.Request.Query, "id"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/lb/clusters/{clusterId}/destinations", (string clusterId, HttpContext ctx,
            IConfiguration config, OutlierDetectionRegistry registry) =>
        {
            var cluster = config.GetSection("ReverseProxy:Clusters").GetChildren()
                .FirstOrDefault(item => string.Equals(item.Key, clusterId, StringComparison.OrdinalIgnoreCase));
            if (cluster == null) return ManagementApiContract.Error(ctx, 404, "not_found", "集群不存在");
            var states = registry.GetSnapshots().Where(item => item.ClusterId == cluster.Key)
                .ToDictionary(item => item.DestinationId, StringComparer.Ordinal);
            var destinations = cluster.GetSection("Destinations").GetChildren().Select(destination =>
            {
                var address = destination["Address"] ?? "";
                Uri.TryCreate(address, UriKind.Absolute, out var uri);
                return new DestinationPageItem(destination.Key, address, uri?.Host ?? "*", uri?.Port ?? 0,
                    uri?.Scheme ?? "http", destination.GetSection("Metadata").GetChildren()
                        .ToDictionary(item => item.Key, item => item.Value ?? ""),
                    DetectConfigSource(config, $"ReverseProxy:Clusters:{cluster.Key}:Destinations:{destination.Key}")
                        == ConfigSource.PatchConfig ? "patch" : "original", states.GetValueOrDefault(destination.Key));
            });
            return Results.Ok(ManagementLists.From(destinations).Sort("id", item => item.Id)
                .Sort("address", item => item.Address).Sort("source", item => item.Source)
                .Search(item => $"{item.Id} {item.Address} {item.Source}").Read(ctx.Request.Query, "id"));
        }).Produces<ManagementPage<DestinationPageItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();
    }
}
