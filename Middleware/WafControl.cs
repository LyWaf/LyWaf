
using LyWaf.Services.Protect;
using LyWaf.Services.Statistic;
using LyWaf.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using NLog;

namespace LyWaf.Middleware;

public class WafControlMiddleware(RequestDelegate next, IStatisticService statisticService, IProtectService protectService)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly IStatisticService statisticService = statisticService;
    private readonly IProtectService protectService = protectService;
    public async Task<bool> WhitePathCheck(HttpContext context)
    {
        var path = await statisticService.GetMatchPath(context.Request.Path);
        if (statisticService.IsWhitePath(path))
        {
            return true;
        }
        return false;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await TryCheckWaf(context);
    }

    public async Task TryCheckWaf(HttpContext context)
    {
        try
        {
            var httpMaxRequestBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (httpMaxRequestBodySizeFeature is not null)
            {
                var max = protectService.GetOptions().MaxRequestBodySize;
                if (max != null)
                {
                    httpMaxRequestBodySizeFeature.MaxRequestBodySize = protectService.GetOptions().MaxRequestBodySize;
                }
            }

            var clientIp = RequestUtil.GetClientIp(context.Request);
            var reason = WafUtil.GetFbReason(clientIp);
            if (reason != null)
            {
                await WafUtil.WriteFbOutput(context, reason);
                return;
            }
            if (await WhitePathCheck(context))
            {
                await _next(context);
                return;
            }
            if ((reason = await CheckArgsAttck(context)) != null)
            {
                await WafUtil.WriteFbOutput(context, reason!);
                return;
            }
            if ((reason = await CheckPostAttck(context)) != null)
            {
                await WafUtil.WriteFbOutput(context, reason!);
                return;
            }
            await _next(context);
        }
        catch (BadHttpRequestException e)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsync(e.Message);
        }
        catch (Exception e)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(e.Message);
        }
    }


    public async Task<string?> CheckArgsAttck(HttpContext context)
    {
        return await protectService.CheckArgsAttck(context);
    }

    public async Task<string?> CheckPostAttck(HttpContext context)
    {
        return await protectService.CheckPostAttck(context);
    }
}