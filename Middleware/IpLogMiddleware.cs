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
            // 先同步捕获请求数据，避免与后续中间件产生竞争条件
            var logEntry = await CaptureRequestAsync(context, clientIp);

            // 异步写入文件（不阻塞请求处理）
            _ = IpRequestLogger.WriteLogEntryAsync(clientIp, logEntry);
        }

        await _next(context);
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

        // 请求体
        if (request.ContentLength > 0 || request.ContentType != null)
        {
            try
            {
                request.EnableBuffering();
                var originalPosition = request.Body.Position;

                using var reader = new StreamReader(
                    request.Body,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true
                );

                var buffer = new char[IpRequestLogger.MaxRequestBytes];
                var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                var bodyContent = new string(buffer, 0, charsRead);

                var bodyTruncated = request.ContentLength > IpRequestLogger.MaxRequestBytes;

                // 恢复 Body 位置，确保后续中间件还能读取
                request.Body.Position = originalPosition;

                if (!string.IsNullOrEmpty(bodyContent))
                {
                    sb.AppendLine("--- Body ---");
                    sb.AppendLine(bodyContent);
                    if (bodyTruncated)
                    {
                        sb.AppendLine($"[... 截断，原始大小约: {request.ContentLength} 字节 ...]");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"--- Body (读取失败: {ex.Message}) ---");
            }
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
