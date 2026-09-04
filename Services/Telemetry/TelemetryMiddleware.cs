using System.Diagnostics;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Services.Telemetry;

public sealed class TelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var activity = LyWafTelemetry.ActivitySource.StartActivity(
            "lywaf.proxy.pipeline",
            ActivityKind.Internal);
        var routeId = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
        activity?.SetTag("lywaf.route.id", routeId ?? "unknown");
        activity?.SetTag("http.request.method", context.Request.Method);

        try
        {
            await next(context);
            activity?.SetTag("http.response.status_code", context.Response.StatusCode);
            var error = context.GetForwarderErrorFeature()?.Error;
            if (error is { } forwarderError && forwarderError != ForwarderError.None)
            {
                activity?.SetTag("lywaf.proxy.error", forwarderError.ToString());
                activity?.SetStatus(ActivityStatusCode.Error, forwarderError.ToString());
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
