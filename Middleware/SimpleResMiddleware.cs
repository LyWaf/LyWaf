using System.Text;
using LyWaf.Services.SimpleRes;
using LyWaf.Utils;
using Microsoft.Extensions.Options;
using NLog;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;

/// <summary>
/// 简单响应中间件
/// 根据路由 ID（simpleres_xxx 格式）返回预配置的静态响应
/// 支持配置热更新
/// 
/// 支持的占位符：
/// - {PORT} / {port} - 请求端口
/// - {HOST} / {host} - 请求主机名
/// - {PATH} / {path} - 请求路径
/// - {METHOD} / {method} - 请求方法
/// - {QUERY} / {query} - 查询字符串
/// - {SCHEME} / {scheme} - 协议（http/https）
/// - {CLIENT_IP} / {ClientIp} - 客户端IP
/// - {TIME} / {time} - 当前时间
/// - {URL} / {url} - 完整URL
/// - {USER_AGENT} / {UserAgent} - User-Agent
/// </summary>
public class SimpleResMiddleware : IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SimpleResOptions> _optionsMonitor;
    private readonly IDisposable? _optionsChangeToken;
    private SimpleResOptions _currentOptions;

    public SimpleResMiddleware(RequestDelegate next, IOptionsMonitor<SimpleResOptions> options)
    {
        _next = next;
        _optionsMonitor = options;
        _currentOptions = options.CurrentValue;
        
        // 监控配置变化
        _optionsChangeToken = _optionsMonitor.OnChange(OnOptionsChanged);
    }

    private void OnOptionsChanged(SimpleResOptions newOptions)
    {
        _logger.Info("SimpleRes 配置已更新，共 {Count} 个响应项", newOptions.Items.Count);
        _currentOptions = newOptions;
    }

    public void Dispose()
    {
        _optionsChangeToken?.Dispose();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 获取当前路由的配置
        var endpoint = context.GetEndpoint();
        var routeModel = endpoint?.Metadata.GetMetadata<RouteModel>();
        var routeId = routeModel?.Config?.RouteId;

        // 检查路由 ID 是否以 simpleres_ 开头
        if (!string.IsNullOrEmpty(routeId) && routeId.StartsWith("simpleres_"))
        {
            var options = _currentOptions;
            
            // 使用路由 ID 作为 key 查找对应的响应配置
            if (options.Items.TryGetValue(routeId, out var item))
            {
                _logger.Debug("SimpleRes matched: RouteId={RouteId}, Path={Path}, Status={Status}", 
                    routeId, context.Request.Path, item.StatusCode);

                // 设置状态码
                context.Response.StatusCode = item.StatusCode;
                
                // 设置 Content-Type
                context.Response.ContentType = item.GetFullContentType();
                
                // 设置额外的响应头
                if (item.Headers != null)
                {
                    foreach (var (headerName, headerValue) in item.Headers)
                    {
                        context.Response.Headers[headerName] = headerValue;
                    }
                }

                // 构建最终响应体（替换占位符）
                var finalBody = FormatResponseBody(item.Body, context);
                
                // 如果启用了显示请求头，则在响应体中追加请求头信息
                if (item.ShowReq)
                {
                    var headersText = new StringBuilder();
                    headersText.AppendLine("\n--- 请求头信息 ---");
                    foreach (var header in context.Request.Headers)
                    {
                        headersText.AppendLine($"{header.Key}: {string.Join(", ", header.Value.ToArray())}");
                    }
                    headersText.AppendLine("--- 请求头信息结束 ---");
                    finalBody += headersText.ToString();
                }

                // 写入响应体
                var encoding = item.GetEncoding();
                var bodyBytes = encoding.GetBytes(finalBody);
                await context.Response.Body.WriteAsync(bodyBytes);
                
                // 短路请求
                return;
            }
            else
            {
                _logger.Warn("SimpleRes config not found for route: {RouteId}", routeId);
            }
        }

        // 不是 SimpleRes 路由，继续执行下一个中间件
        await _next(context);
    }

    /// <summary>
    /// 格式化响应体，替换占位符
    /// 使用单次遍历方式提高效率
    /// </summary>
    private static string FormatResponseBody(string body, HttpContext context)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        var openBrace = body.IndexOf('{');
        if (openBrace < 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length + 64);
        var request = context.Request;
        var lastPos = 0;

        // 延迟计算的值（只在需要时计算）
        string? clientIp = null;
        string? fullUrl = null;
        string? userAgent = null;
        string? routeId = null;
        DateTime? now = null;

        while (openBrace >= 0)
        {
            var closeBrace = body.IndexOf('}', openBrace + 1);
            if (closeBrace < 0)
            {
                break;
            }

            // 添加 { 之前的内容
            if (openBrace > lastPos)
            {
                sb.Append(body, lastPos, openBrace - lastPos);
            }

            // 提取占位符名称
            var placeholder = body.AsSpan(openBrace + 1, closeBrace - openBrace - 1);

            // 匹配占位符（不区分大小写）
            if (placeholder.Equals("PORT", StringComparison.OrdinalIgnoreCase) ||
                placeholder.Equals("Port", StringComparison.Ordinal))
            {
                sb.Append(request.Host.Port?.ToString() ?? "80");
            }
            else if (placeholder.Equals("HOST", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Host", StringComparison.Ordinal))
            {
                sb.Append(request.Host.Host);
            }
            else if (placeholder.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Path", StringComparison.Ordinal))
            {
                sb.Append(request.Path.Value ?? "/");
            }
            else if (placeholder.Equals("METHOD", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Method", StringComparison.Ordinal))
            {
                sb.Append(request.Method);
            }
            else if (placeholder.Equals("QUERY", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Query", StringComparison.Ordinal))
            {
                sb.Append(request.QueryString.Value ?? "");
            }
            else if (placeholder.Equals("SCHEME", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Scheme", StringComparison.Ordinal))
            {
                sb.Append(request.Scheme);
            }
            else if (placeholder.Equals("CLIENT_IP", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("ClientIp", StringComparison.Ordinal) ||
                     placeholder.Equals("ClientIP", StringComparison.Ordinal) ||
                     placeholder.Equals("IP", StringComparison.OrdinalIgnoreCase))
            {
                clientIp ??= RequestUtil.GetClientIp(request);
                sb.Append(clientIp);
            }
            else if (placeholder.Equals("TIME", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Time", StringComparison.Ordinal))
            {
                now ??= DateTime.Now;
                sb.Append(now.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else if (placeholder.Equals("DATE", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Date", StringComparison.Ordinal))
            {
                now ??= DateTime.Now;
                sb.Append(now.Value.ToString("yyyy-MM-dd"));
            }
            else if (placeholder.Equals("URL", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("Url", StringComparison.Ordinal))
            {
                fullUrl ??= $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";
                sb.Append(fullUrl);
            }
            else if (placeholder.Equals("USER_AGENT", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("UserAgent", StringComparison.Ordinal))
            {
                userAgent ??= request.Headers.UserAgent.ToString();
                sb.Append(userAgent);
            }
            else if (placeholder.Equals("ROUTE_ID", StringComparison.OrdinalIgnoreCase) ||
                     placeholder.Equals("RouteId", StringComparison.Ordinal))
            {
                routeId ??= context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config?.RouteId ?? "";
                sb.Append(routeId);
            }
            else
            {
                // 未知占位符，保留原样
                sb.Append('{');
                sb.Append(placeholder);
                sb.Append('}');
            }

            lastPos = closeBrace + 1;
            openBrace = body.IndexOf('{', lastPos);
        }

        // 添加剩余内容
        if (lastPos < body.Length)
        {
            sb.Append(body, lastPos, body.Length - lastPos);
        }

        return sb.ToString();
    }
}
