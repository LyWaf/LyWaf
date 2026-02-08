using LyWaf.Services;
using LyWaf.Services.ErrorTemplate;
using LyWaf.Services.Statistic;
using LyWaf.Shared;

namespace LyWaf.Utils;

public class WafUtil
{
    /// <summary>
    /// 格式化消息，替换占位符
    /// 支持的占位符:
    ///   {ClientIp} - 客户端IP
    ///   {Path} - 请求路径
    ///   {Method} - 请求方法
    ///   {Host} - 请求Host
    ///   {Time} - 当前时间
    ///   以及其他 HttpContext 相关的占位符
    /// </summary>
    public static string FormatMessage(string? message, HttpContext context, Dictionary<string, string?>? extraValues = null)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? "";

        return StringUtil.FormatTemplate(message, context, extraValues);
    }

    public static void DoFbIp(string ip, string reason, TimeSpan? timeout = null, bool isAttack = true, 
        SecurityEventType? eventType = null)
    {
        if (timeout == null)
        {
            var statisticService = ServiceLocator.GetRequiredService<IStatisticService>();
            if (statisticService == null)
            {
                timeout = TimeSpan.FromSeconds(600);
            }
            else
            {
                timeout = statisticService.GetOption().GetDefaultFbTime();
            }
        }
        SharedData.ClientFb.AddOrUpdate(ip, reason, timeout);
        
        // 记录攻击IP（封禁的IP视为攻击来源）
        if (isAttack)
        {
            SharedData.Traffic.RecordAttackIp(ip);
        }
        
        // 记录安全事件
        var secEventType = eventType ?? SecurityEventType.CcAttack;
        SharedData.Security.RecordEvent(secEventType, ip);
    }

    public static string? GetFbReason(string ip)
    {
        SharedData.ClientFb.TryGetValue(ip, out var reason);
        return reason;
    }

    /// <summary>
    /// 写入 403 Forbidden 错误响应
    /// </summary>
    public static Task WriteFbOutput(HttpContext context, Dictionary<string, string?>? extraValues = null, 
        bool isAttack = false, SecurityEventType? eventType = null)
        => WriteErrorOutput(context, 403, extraValues, isIntercept: true, isAttack: isAttack, eventType: eventType);

    /// <summary>
    /// 写入指定状态码的错误响应（统一入口）
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <param name="statusCode">HTTP 状态码</param>
    /// <param name="extraValues">额外的占位符值（支持 reason 等任意参数）</param>
    /// <param name="isIntercept">是否为主动拦截（用于流量统计）</param>
    /// <param name="isAttack">是否为攻击请求（用于流量统计）</param>
    /// <param name="eventType">安全事件类型（用于安全态势统计）</param>
    public static async Task WriteErrorOutput(HttpContext context, int statusCode, 
        Dictionary<string, string?>? extraValues = null, bool isIntercept = true, bool isAttack = false,
        SecurityEventType? eventType = null)
    {
        // 记录拦截统计
        if (isIntercept)
        {
            StatisticUtil.RecordIntercept(context, statusCode, isAttack);
            
            // 记录安全事件
            if (eventType.HasValue)
            {
                var clientIp = RequestUtil.GetClientIp(context.Request);
                SharedData.Security.RecordEvent(eventType.Value, clientIp);
            }
        }
        
        var templateService = ServiceLocator.GetService<IErrorTemplateService>();
        if (templateService != null)
        {
            await templateService.WriteErrorResponseAsync(context, statusCode, extraValues);
        }
        else
        {
            await WriteFallbackError(context, statusCode);
        }
    }

    /// <summary>
    /// 写入 400 Bad Request 错误响应
    /// </summary>
    public static Task WriteBadRequest(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 400, extraValues);

    /// <summary>
    /// 写入 401 Unauthorized 错误响应
    /// </summary>
    public static Task WriteUnauthorized(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 401, extraValues);

    /// <summary>
    /// 写入 404 Not Found 错误响应
    /// </summary>
    public static Task WriteNotFound(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 404, extraValues);

    /// <summary>
    /// 写入 405 Method Not Allowed 错误响应
    /// </summary>
    public static Task WriteMethodNotAllowed(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 405, extraValues);

    /// <summary>
    /// 写入 429 Too Many Requests 错误响应
    /// </summary>
    public static Task WriteTooManyRequests(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 429, extraValues);

    /// <summary>
    /// 写入 500 Internal Server Error 错误响应
    /// </summary>
    public static Task WriteInternalError(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 500, extraValues);

    /// <summary>
    /// 写入 502 Bad Gateway 错误响应
    /// </summary>
    public static Task WriteBadGateway(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 502, extraValues);

    /// <summary>
    /// 写入 503 Service Unavailable 错误响应
    /// </summary>
    public static Task WriteServiceUnavailable(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 503, extraValues);

    /// <summary>
    /// 写入 504 Gateway Timeout 错误响应
    /// </summary>
    public static Task WriteGatewayTimeout(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorOutput(context, 504, extraValues);

    /// <summary>
    /// 降级的通用错误响应
    /// </summary>
    private static async Task WriteFallbackError(HttpContext context, int statusCode)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";

        var html = $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>{statusCode} Error - LyWaf</title>
            </head>
            <body style="font-family: sans-serif; text-align: center; padding: 50px;">
                <h1>{statusCode} Error</h1>
                <p>请求处理失败，请稍后再试。</p>
                <hr>
                <small>LyWaf</small>
            </body>
            </html>
            """;

        await context.Response.WriteAsync(html);
    }
}
