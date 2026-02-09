using LyWaf.Services.Statistic;
using LyWaf.Utils;
using NLog;

namespace LyWaf.Middleware;

/// <summary>
/// CC 防护中间件
/// 检测并处理 CC 攻击
/// </summary>
public class CcProtectionMiddleware(RequestDelegate next, ICcRuleChecker ccRuleChecker)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly ICcRuleChecker _ccRuleChecker = ccRuleChecker;

    public async Task InvokeAsync(HttpContext context)
    {
        var clientIp = RequestUtil.GetClientIp(context.Request);
        
        // 检查 CC 规则
        var result = _ccRuleChecker.CheckRequest(context, clientIp);
        
        if (result.IsTriggered)
        {
            // 如果动作不是 LogOnly，则执行处罚并返回
            if (result.Action != CcAction.LogOnly)
            {
                await _ccRuleChecker.ExecuteAction(context, result, clientIp);
                return;
            }
            
            // LogOnly 模式下继续处理请求，只记录日志
            await _ccRuleChecker.ExecuteAction(context, result, clientIp);
        }
        
        await _next(context);
    }
}

/// <summary>
/// CC 响应监控中间件
/// 用于记录错误响应，支持 FrequentError 类型规则
/// </summary>
public class CcResponseMonitorMiddleware(RequestDelegate next, ICcRuleChecker ccRuleChecker)
{
    private readonly RequestDelegate _next = next;
    private readonly ICcRuleChecker _ccRuleChecker = ccRuleChecker;

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);
        
        // 记录错误响应
        var statusCode = context.Response.StatusCode;
        if (statusCode >= 400)
        {
            var clientIp = RequestUtil.GetClientIp(context.Request);
            _ccRuleChecker.RecordErrorEvent(clientIp, statusCode);
        }
    }
}
