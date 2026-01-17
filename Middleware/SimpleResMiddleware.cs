using System.Text;
using LyWaf.Services.SimpleRes;
using Microsoft.Extensions.Options;
using NLog;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;

/// <summary>
/// 简单响应中间件
/// 根据路由 ID（simpleres_xxx 格式）返回预配置的静态响应
/// 支持配置热更新
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

                // 构建最终响应体
                var finalBody = item.Body;
                
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
                    finalBody = item.Body + headersText.ToString();
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
}
