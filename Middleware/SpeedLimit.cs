
using Microsoft.Net.Http.Headers;
using LyWaf.Services.Files;
using NLog;
using System.Text.Unicode;
using System.Text;
using Yarp.ReverseProxy.Model;
using LyWaf.Services.SpeedLimit;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace LyWaf.Middleware;

public class SpeedLimitMiddleware(RequestDelegate next)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var end = context.GetEndpoint()!;
        var meta = end.Metadata;
        var route = meta.GetMetadata<RouteModel>();
        if (route != null && route.Config.Metadata != null)
        {
            var speedService = context.RequestServices.GetRequiredService<ISpeedLimitService>();
            if (route.Config.Metadata.TryGetValue("RateLimiter", out var policy))
            {
                var rate = speedService.Get(policy);
                if (rate == null)
                {
                    _logger.Warn("不存在策略:{}", policy);
                }
                else
                {
                    using RateLimitLease lease = await rate.AcquireAsync(context, 1);
                    if (!lease.IsAcquired)
                    {
                        var reject = speedService.GetRejectCode();
                        context.Response.StatusCode = reject;
                        return;
                    }
                }
            }
        }
        await _next(context);
    }
}