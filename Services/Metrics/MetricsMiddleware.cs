using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Forwarder;

namespace LyWaf.Services.Metrics;

public sealed class MetricsMiddleware(RequestDelegate next, PrometheusMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!metrics.Enabled)
        {
            await next(context);
            return;
        }

        var routeId = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
        var observation = metrics.StartRequest(routeId);
        var originalBody = context.Response.Body;
        var countingBody = new CountingWriteStream(originalBody);
        context.Response.Body = countingBody;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
            observation.Complete(context.Request.Method, context.Response.StatusCode,
                context.Request.ContentLength ?? 0, countingBody.BytesWritten);
            var proxyFeature = context.GetReverseProxyFeature();
            var destination = proxyFeature.ProxiedDestination;
            if (destination != null)
                metrics.SetUpstreamConcurrentRequests(
                    proxyFeature.Cluster.Config.ClusterId,
                    destination.Model.Config.Address,
                    destination.ConcurrentRequestCount);

            var error = context.GetForwarderErrorFeature()?.Error;
            if (error is { } forwarderError && forwarderError != ForwarderError.None)
                metrics.RecordProxyError(forwarderError.ToString());
        }
    }
}
