using LyWaf.Services.SpeedLimit;
using LyWaf.Utils;
using NLog;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;

/// <summary>
/// IP访问控制和连接限制中间件
/// 处理基于IP的访问控制（黑白名单）和连接数限制
/// </summary>
public class AccessControlMiddleware(RequestDelegate next, ISpeedLimitService speedLimitService)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly ISpeedLimitService _speedLimitService = speedLimitService;

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _speedLimitService.GetOptions();
        var clientIp = RequestUtil.GetClientIp(context.Request);
        var path = context.Request.Path.Value ?? "/";

        // 1. IP访问控制检查
        if (options.AccessControl.Enabled)
        {
            if (!_speedLimitService.IsIpAllowed(clientIp, path))
            {
                _logger.Warn("IP访问被拒绝: {ClientIp}, Path: {Path}", clientIp, path);
                context.Response.StatusCode = options.AccessControl.RejectStatusCode;
                context.Response.ContentType = "text/plain; charset=utf-8";
                var message = WafUtil.FormatMessage(options.AccessControl.RejectMessage, context);
                await context.Response.WriteAsync(message);
                return;
            }
        }

        // 2. 连接限制检查
        if (options.ConnectionLimit.Enabled)
        {
            // 获取目标服务器地址（如果有）
            string? destination = null;
            var endpoint = context.GetEndpoint();
            if (endpoint != null)
            {
                var route = endpoint.Metadata.GetMetadata<RouteModel>();
                // 目标地址将在代理过程中确定，这里暂时使用路由信息
                destination = route?.Config.ClusterId;
            }

            if (!_speedLimitService.TryAcquireConnection(clientIp, destination, path))
            {
                _logger.Warn("连接数超限: ClientIp={ClientIp}, Destination={Destination}, Path={Path}",
                    clientIp, destination, path);
                context.Response.StatusCode = options.ConnectionLimit.RejectStatusCode;
                context.Response.ContentType = "text/plain; charset=utf-8";
                var message = WafUtil.FormatMessage(options.ConnectionLimit.RejectMessage, context);
                await context.Response.WriteAsync(message);
                return;
            }

            try
            {
                await _next(context);
            }
            finally
            {
                // 请求完成后释放连接
                _speedLimitService.ReleaseConnection(clientIp, destination, path);
            }
        }
        else
        {
            await _next(context);
        }
    }
}
