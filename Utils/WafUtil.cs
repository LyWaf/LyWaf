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
    /// </summary>
    public static string FormatMessage(string message, HttpContext context)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var result = message;
        var clientIp = RequestUtil.GetClientIp(context.Request);

        result = result.Replace("{ClientIp}", clientIp);
        result = result.Replace("{Path}", context.Request.Path.Value ?? "/");
        result = result.Replace("{Method}", context.Request.Method);
        result = result.Replace("{Host}", context.Request.Host.ToString());
        result = result.Replace("{Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        return result;
    }

    public static void DoFbIp(string ip, string reason, TimeSpan? timeout = null)
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
    }

    public static string? GetFbReason(string ip)
    {
        SharedData.ClientFb.TryGetValue(ip, out var reason);
        return reason;
    }

    /// <summary>
    /// 写入 403 Forbidden 错误响应
    /// 使用错误模板服务（如果可用）
    /// </summary>
    public static async Task WriteFbOutput(HttpContext context, string reason)
    {
        var templateService = ServiceLocator.GetService<IErrorTemplateService>();
        if (templateService != null)
        {
            await templateService.WriteForbiddenAsync(context, reason);
        }
        else
        {
            // 降级到简单响应
            await WriteFallbackForbidden(context, reason);
        }
    }

    /// <summary>
    /// 写入指定状态码的错误响应（核心方法）
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <param name="statusCode">HTTP 状态码</param>
    /// <param name="reason">错误原因（可选）</param>
    public static async Task WriteErrorOutput(HttpContext context, int statusCode, string? reason = null)
    {
        var templateService = ServiceLocator.GetService<IErrorTemplateService>();
        if (templateService != null)
        {
            await templateService.WriteErrorResponseAsync(context, statusCode, reason);
        }
        else
        {
            await WriteFallbackError(context, statusCode, reason);
        }
    }

    /// <summary>
    /// 写入 400 Bad Request 错误响应
    /// </summary>
    public static Task WriteBadRequest(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 400, reason);

    /// <summary>
    /// 写入 401 Unauthorized 错误响应
    /// </summary>
    public static Task WriteUnauthorized(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 401, reason);

    /// <summary>
    /// 写入 404 Not Found 错误响应
    /// </summary>
    public static Task WriteNotFound(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 404, reason);

    /// <summary>
    /// 写入 405 Method Not Allowed 错误响应
    /// </summary>
    public static Task WriteMethodNotAllowed(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 405, reason);

    /// <summary>
    /// 写入 429 Too Many Requests 错误响应
    /// </summary>
    public static Task WriteTooManyRequests(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 429, reason);

    /// <summary>
    /// 写入 500 Internal Server Error 错误响应
    /// </summary>
    public static Task WriteInternalError(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 500, reason);

    /// <summary>
    /// 写入 502 Bad Gateway 错误响应
    /// </summary>
    public static Task WriteBadGateway(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 502, reason);

    /// <summary>
    /// 写入 503 Service Unavailable 错误响应
    /// </summary>
    public static Task WriteServiceUnavailable(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 503, reason);

    /// <summary>
    /// 写入 504 Gateway Timeout 错误响应
    /// </summary>
    public static Task WriteGatewayTimeout(HttpContext context, string? reason = null)
        => WriteErrorOutput(context, 504, reason);

    /// <summary>
    /// 降级的 403 错误响应（当模板服务不可用时）
    /// </summary>
    private static async Task WriteFallbackForbidden(HttpContext context, string reason)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        var ip = RequestUtil.GetClientIp(context.Request);

        var html = $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>403 Forbidden - LyWaf</title>
            </head>
            <body style="font-family: sans-serif; text-align: center; padding: 50px;">
                <h1>403 Forbidden</h1>
                <p>您的IP为: {ip}</p>
                <p>您的IP存在异常访问的情况，若误封请联系管理员。</p>
            #if DEBUG
                <p style="color: #999;">原因: {reason}</p>
            #endif
                <hr>
                <small>LyWaf</small>
            </body>
            </html>
            """;

        await context.Response.WriteAsync(html);
    }

    /// <summary>
    /// 降级的通用错误响应
    /// </summary>
    private static async Task WriteFallbackError(HttpContext context, int statusCode, string? reason)
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
