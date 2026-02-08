
using LyWaf.Services.LyLog;
using LyWaf.Services.Protect;
using LyWaf.Services.Statistic;
using LyWaf.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using NLog;

namespace LyWaf.Middleware;

public class WafControlMiddleware(RequestDelegate next, IStatisticService statisticService, IProtectService protectService, ILyLogService logService)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly IStatisticService statisticService = statisticService;
    private readonly IProtectService protectService = protectService;
    private readonly ILyLogService _logService = logService;
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
        // 获取域名
        var domain = context.Request.Host.Host;
        var path = context.Request.Path.Value ?? "/";

        // 检查是否需要记录日志
        if (_logService.ShouldLog(domain, path))
        {
            // 获取该域名的 Logger 并注入到 HttpContext
            var logger = _logService.GetLogger(domain);
            context.SetDomainLogger(logger, domain, _logService.GetFormat(domain));
        }

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
                // 被封禁的 IP 访问，记录为黑名单拦截
                await WafUtil.WriteFbOutput(context, new Dictionary<string, string?> { ["reason"] = reason }, 
                    isAttack: false, eventType: Shared.SecurityEventType.BlacklistBlock);
                return;
            }
            if (await WhitePathCheck(context))
            {
                await _next(context);
                return;
            }
            if ((reason = await CheckArgsAttck(context)) != null)
            {
                // WAF Args 攻击检测，标记为攻击
                await WafUtil.WriteFbOutput(context, new Dictionary<string, string?> { ["reason"] = reason }, 
                    isAttack: true, eventType: Shared.SecurityEventType.WafIntercept);
                return;
            }
            if ((reason = await CheckPostAttck(context)) != null)
            {
                // WAF Post 攻击检测，标记为攻击
                await WafUtil.WriteFbOutput(context, new Dictionary<string, string?> { ["reason"] = reason }, 
                    isAttack: true, eventType: Shared.SecurityEventType.WafIntercept);
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