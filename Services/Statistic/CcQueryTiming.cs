using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.Statistic;

internal sealed record CcQueryTiming(DateTimeOffset ReceivedAt, long Started)
{
    internal static void Capture(HttpContext context) => NodeRequestTiming.Capture(context);
    internal static CcQueryTiming? Read(HttpContext context) => NodeRequestTiming.Read(context) is { } value
        ? new(value.ReceivedAt, value.Started) : null;
}

// Capture locally before authentication, never from caller-provided headers.
public sealed class CcQueryTimingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Path == NodeMessageAuthentication.Prefix + CcExecutionQueryService.Operation) CcQueryTiming.Capture(context);
            await continuation(context);
        });
        next(app);
    };
}
