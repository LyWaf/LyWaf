using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.Statistic;

internal sealed record CcQueryTiming(DateTimeOffset ReceivedAt, long Started)
{
    private static readonly object Key = new();
    internal static void Capture(HttpContext context) => context.Items[Key] = new CcQueryTiming(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    internal static CcQueryTiming? Read(HttpContext context) => context.Items.TryGetValue(Key, out var value) ? value as CcQueryTiming : null;
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
