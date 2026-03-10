
using LyWaf.Plugins.Examples;
using LyWaf.Services.AccessControl;
using LyWaf.Services.LyLog;
using LyWaf.Services.Protect;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafRule;
using LyWaf.Shared;
using LyWaf.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using NLog;

namespace LyWaf.Middleware;

public class WafControlMiddleware(RequestDelegate next, IStatisticService statisticService, IProtectService protectService, ILyLogService logService, ICcRuleChecker ccRuleChecker, IWafRuleService wafRuleService, IAccessControlService accessControlService)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly IStatisticService statisticService = statisticService;
    private readonly IProtectService protectService = protectService;
    private readonly ILyLogService _logService = logService;
    private readonly ICcRuleChecker _ccRuleChecker = ccRuleChecker;
    private readonly IWafRuleService _wafRuleService = wafRuleService;
    private readonly IAccessControlService _accessControlService = accessControlService;

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

    /// <summary>
    /// 设置拦截日志标记，让 RequestLoggerPlugin 在 RequestCompletedEvent 中记录
    /// </summary>
    private static void SetInterceptLogMark(HttpContext context, string reason)
    {
        context.Items["RequestLogger.InterceptRule"] = new MatchRule
        {
            Host = "*",
            FilePrefix = "intercept",
            LogQueryString = true,
            LogRequestBody = true,
            LogHeaders = false,
            LogResponseBody = false,
            LogDuration = false,
        };
        context.Items["RequestLogger.Reason"] = reason;
    }

    /// <summary>
    /// 记录 WAF 拦截到检测事件统计
    /// </summary>
    private void RecordWafHit(HttpContext context, string clientIp, string reason)
    {
        var geoInfo = _accessControlService.GetGeoInfo(clientIp);
        var host = context.Request.Host.Host;
        SharedData.RecordInterceptEvent(clientIp, host,
            geoInfo?.Region ?? "", geoInfo?.City ?? "",
            reason, "Black");
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
                RecordWafHit(context, clientIp, $"封禁IP: {reason}");
                await WafUtil.WriteFbOutput(context, new Dictionary<string, string?> { ["reason"] = reason },
                    isAttack: false, eventType: SecurityEventType.BlacklistBlock);
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
                _ccRuleChecker.RecordAttackEvent(clientIp); // 记录攻击事件用于 CC 高频攻击检测
                RecordWafHit(context, clientIp, reason);
                SetInterceptLogMark(context, reason);
                await WafUtil.WriteFbOutput(context, new Dictionary<string, string?> { ["reason"] = reason },
                    isAttack: true, eventType: SecurityEventType.WafIntercept);
                return;
            }
            if ((reason = await CheckPostAttck(context)) != null)
            {
                // WAF Post 攻击检测，标记为攻击
                _ccRuleChecker.RecordAttackEvent(clientIp); // 记录攻击事件用于 CC 高频攻击检测
                RecordWafHit(context, clientIp, reason);
                SetInterceptLogMark(context, reason);
                await WafUtil.WriteFbOutput(context, new Dictionary<string, string?> { ["reason"] = reason },
                    isAttack: true, eventType: SecurityEventType.WafIntercept);
                return;
            }

            // ── 自定义 WAF 规则检查（用户规则 + 配置规则 + 系统规则）──
            var ruleResult = _wafRuleService.CheckRequest(context, clientIp);
            if (ruleResult.IsTriggered)
            {
                if (ruleResult.Action != WafRuleAction.Observe)
                {
                    var wafReason = $"WAF规则: {ruleResult.TriggeredRule?.Name}";
                    RecordWafHit(context, clientIp, wafReason);
                    SetInterceptLogMark(context, wafReason);
                    await _wafRuleService.ExecuteAction(context, ruleResult, clientIp);
                    return;
                }
                // Observe：仅记录，继续管道
                await _wafRuleService.ExecuteAction(context, ruleResult, clientIp);
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
