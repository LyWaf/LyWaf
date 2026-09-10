using System.ComponentModel.DataAnnotations;
using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;
using Yarp.ReverseProxy;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record UpstreamHealthResponse(bool Success, UpstreamHealthSettings Data);
    private sealed record UpstreamHealthSaveResponse(bool Success, string Message);
    public sealed record UpstreamHealthSettings(
        bool Enabled = true,
        [property: Required, RegularExpression(@"^/[^\r\n?#]*$"), MaxLength(2048)] string Path = "/",
        [property: Required, RegularExpression("^(GET|HEAD|POST|PUT|PATCH|DELETE|OPTIONS)$")] string Method = "GET",
        [property: MaxLength(65536)] string Body = "",
        [property: Required, RegularExpression("^(application/json|application/x-www-form-urlencoded|text/plain)$")] string ContentType = "application/json",
        [property: Required, MaxLength(1024), RegularExpression(LyWaf.Policy.HealthStatusCodes.Pattern)] string StatusCodes = "2xx",
        [property: Range(1, 3600)] int IntervalSeconds = 30,
        [property: Range(1, 300)] int TimeoutSeconds = 5,
        [property: Range(1, 100)] int Passes = 2,
        [property: Range(1, 100)] int Fails = 2);

    private static void MapUpstreamHealthEndpoints(WebApplication app, int controlPort)
    {
        var group = app.MapGroup("/api/lb/clusters/{clusterId}/health-check")
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        group.MapGet("", (string clusterId, IProxyStateLookup runtime) =>
        {
            var cluster = runtime.GetClusters().FirstOrDefault(c => c.ClusterId == clusterId)?.Model.Config;
            if (cluster == null || clusterId == "cluster_unuse") return Results.NotFound();
            var active = cluster.HealthCheck?.Active;
            string Read(string key, string fallback) => cluster.Metadata?.GetValueOrDefault("LyxActiveHealth." + key) ?? fallback;
            int Number(string key) => int.TryParse(Read(key, "2"), out var value) ? value : 2;
            return Results.Ok(new { success = true, data = new UpstreamHealthSettings(active?.Enabled == true,
                active?.Path ?? "/", Read("Method", "GET"), Read("Body", ""), Read("ContentType", "application/json"),
                Read("AvalidCode", "2xx"), (int)(active?.Interval?.TotalSeconds ?? 30), (int)(active?.Timeout?.TotalSeconds ?? 5), Number("Passes"), Number("Fails")) });
        }).Produces<UpstreamHealthResponse>();
        group.MapPut("", async (string clusterId, UpstreamHealthSettings settings, IConfiguration config) =>
        {
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(settings, new ValidationContext(settings), errors, true))
                return Results.BadRequest(new { success = false, message = string.Join("; ", errors.Select(e => e.ErrorMessage)) });
            if (clusterId == "cluster_unuse" || !config.GetSection($"ReverseProxy:Clusters:{clusterId}").Exists())
                return Results.NotFound();
            return await ModifyLbPatch(config, patch =>
            {
                var cluster = EnsurePatchCluster(patch, clusterId);
                // Only replace active settings; passive checks and unrelated metadata remain intact.
                if (!cluster.TryGetValue("HealthCheck", out var value) || value is not Dictionary<string, object> health)
                    cluster["HealthCheck"] = health = new();
                health["Active"] = new Dictionary<string, object>
                {
                    ["Enabled"] = settings.Enabled, ["Policy"] = "LyxActiveHealth", ["Path"] = settings.Path,
                    ["Interval"] = TimeSpan.FromSeconds(settings.IntervalSeconds).ToString("c"),
                    ["Timeout"] = TimeSpan.FromSeconds(settings.TimeoutSeconds).ToString("c")
                };
                if (!cluster.TryGetValue("Metadata", out value) || value is not Dictionary<string, object> metadata)
                    cluster["Metadata"] = metadata = new();
                metadata["LyxActiveHealth.Method"] = settings.Method;
                metadata["LyxActiveHealth.Body"] = settings.Body;
                metadata["LyxActiveHealth.ContentType"] = settings.ContentType;
                metadata["LyxActiveHealth.AvalidCode"] = settings.StatusCodes;
                metadata["LyxActiveHealth.Passes"] = settings.Passes.ToString();
                metadata["LyxActiveHealth.Fails"] = settings.Fails.ToString();
                return null;
            });
        }).Produces<UpstreamHealthSaveResponse>().WithMetadata(new ManagementStepUpExemption());
    }
}
