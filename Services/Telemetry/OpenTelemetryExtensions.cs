using System.Diagnostics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LyWaf.Services.Telemetry;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddLyWafOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry");
        services.Configure<OpenTelemetryOptions>(section);
        var options = section.Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();
        if (!options.Enabled) return services;

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName) ? "lywaf" : options.ServiceName.Trim();
        var endpoint = new Uri(options.Endpoint, UriKind.Absolute);
        var protocol = options.Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
        var sampleRatio = Math.Clamp(options.SampleRatio, 0, 1);
        var excludedPaths = options.ExcludedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.StartsWith('/') ? path : "/" + path)
            .ToArray();

        var openTelemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName));

        if (options.ExportTraces)
        {
            openTelemetry.WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)))
                .AddSource(LyWafTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    instrumentation.Filter = context => !excludedPaths.Any(path =>
                        context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase));
                    instrumentation.EnrichWithHttpRequest = SanitizeInboundRequest;
                })
                .AddHttpClientInstrumentation(instrumentation =>
                {
                    instrumentation.EnrichWithHttpRequestMessage = SanitizeOutboundRequest;
                })
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = endpoint;
                    exporter.Protocol = protocol;
                }));
        }

        if (options.ExportMetrics)
        {
            openTelemetry.WithMetrics(metrics => metrics
                .AddMeter(LyWafTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = endpoint;
                    exporter.Protocol = protocol;
                }));
        }

        return services;
    }

    private static void SanitizeInboundRequest(Activity activity, HttpRequest request)
    {
        activity.SetTag("url.query", null);
        activity.SetTag("http.target", request.Path.Value);
    }

    private static void SanitizeOutboundRequest(Activity activity, HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri) return;
        var sanitized = uri.GetLeftPart(UriPartial.Path);
        activity.SetTag("url.full", sanitized);
        activity.SetTag("http.url", sanitized);
        activity.SetTag("url.query", null);
    }
}
