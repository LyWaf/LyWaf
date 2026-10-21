using System.Diagnostics;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using LyWaf.Services.Distribution;

namespace LyWaf.Services.Telemetry;

public sealed class TelemetryMiddleware(RequestDelegate next, NodeTelemetryCollector? telemetry = null)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;
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
            failed = true;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            telemetry?.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.Response.StatusCode,
                context.Items.TryGetValue("LyWaf.Intercepted", out var intercepted) && intercepted is true, failed,
                LyWaf.Utils.RequestUtil.GetClientIp(context.Request));
        }
    }
}
