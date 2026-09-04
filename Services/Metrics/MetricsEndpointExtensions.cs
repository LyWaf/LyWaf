using LyWaf.Plugins.Core;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Metrics;

public static class MetricsEndpointExtensions
{
    public static WebApplication MapLyWafMetrics(this WebApplication app, int controlPort)
    {
        var configuredPath = app.Services.GetRequiredService<IOptions<MetricsOptions>>().Value.Path;
        var path = NormalizePath(configuredPath);

        app.MapGet(path, (HttpContext context, PrometheusMetrics metrics, IPluginManager pluginManager) =>
        {
            if (!metrics.Enabled) return Results.NotFound();

            var accept = context.Request.GetTypedHeaders().Accept;
            var openMetrics = accept?.Any(static value =>
                value.MediaType.Value?.Equals("application/openmetrics-text", StringComparison.OrdinalIgnoreCase) == true) == true;
            var contentType = openMetrics
                ? "application/openmetrics-text; version=1.0.0; charset=utf-8"
                : "text/plain; version=0.0.4; charset=utf-8";
            return Results.Text(metrics.Render(openMetrics, pluginManager), contentType);
        }).RequireHost($"*:{controlPort}");

        return app;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/metrics";
        var normalized = path.Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }
}
