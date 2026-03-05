using System.Diagnostics;
using System.Text;
using LyWaf.Shared;
using LyWaf.Utils;

namespace LyWaf.Middleware;

/// <summary>
/// IP 请求日志中间件
/// 检查客户端 IP 是否在日志监控列表中，如果是则记录完整请求内容到文件
/// 放置于 AccessControlMiddleware 之后，确保能捕获所有通过访问控制的请求
/// </summary>
public class IpLogMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var clientIp = RequestUtil.GetClientIp(context.Request);

        if (SharedData.IpLogTargets.TryGetValue(clientIp, out _))
        {
            // 先同步捕获请求数据
            var requestPart = await CaptureRequestAsync(context, clientIp);

            // 捕获响应体 + 计时
            var sw = Stopwatch.StartNew();
            var responseBody = await HttpUtil.CaptureResponseBodyAsync(context, () => _next(context));
            sw.Stop();

            // 组合完整日志并异步写入
            var logEntry = AppendResponseInfo(requestPart, context.Response.StatusCode, sw.Elapsed, responseBody);
            _ = IpRequestLogger.WriteLogEntryAsync(clientIp, logEntry);
        }
        else
        {
            await _next(context);
        }
    }

    /// <summary>
    /// 捕获请求数据为字符串（同步在当前上下文中执行）
    /// </summary>
    private static async Task<string> CaptureRequestAsync(HttpContext context, string clientIp)
    {
        var request = context.Request;
        var sb = new StringBuilder();

        // 请求行
        sb.AppendLine($"========== [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ==========");
        sb.AppendLine($"{request.Method} {request.Path}{request.QueryString} {request.Protocol}");
        sb.AppendLine($"Host: {request.Host}");
        sb.AppendLine($"Client-IP: {clientIp}");

        // 请求头
        sb.AppendLine("--- Headers ---");
        foreach (var header in request.Headers)
        {
            sb.AppendLine($"{header.Key}: {header.Value}");
        }

        // 请求体：通过 HttpUtil 统一捕获并缓存
        var bodyContent = await HttpUtil.CaptureRequestBodyAsync(context, IpRequestLogger.MaxRequestBytes);
        if (!string.IsNullOrEmpty(bodyContent))
        {
            sb.AppendLine("--- Body ---");
            sb.AppendLine(bodyContent);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 在请求部分后面追加响应信息（状态码、耗时、响应体）
    /// </summary>
    private static string AppendResponseInfo(string requestPart, int statusCode, TimeSpan elapsed, string? responseBody)
    {
        var sb = new StringBuilder(requestPart);
        var durationStr = elapsed.TotalMilliseconds < 1000
            ? $"{elapsed.TotalMilliseconds:F1}ms"
            : $"{elapsed.TotalSeconds:F2}s";
        sb.AppendLine($"Status: {statusCode} | Duration: {durationStr}");

        if (!string.IsNullOrEmpty(responseBody))
        {
            sb.AppendLine("--- Response Body ---");
            sb.AppendLine(responseBody);
        }

        sb.AppendLine();

        // 限制总输出
        var content = sb.ToString();
        if (Encoding.UTF8.GetByteCount(content) > IpRequestLogger.MaxRequestBytes)
        {
            content = content[..Math.Min(content.Length, IpRequestLogger.MaxRequestBytes / 2)] +
                      "\n[... 日志条目截断 ...]\n\n";
        }

        return content;
    }
}
