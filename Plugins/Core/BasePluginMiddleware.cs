using System.Diagnostics;
using LyWaf.Shared;
using LyWaf.Services.WafInfo;
using LyWaf.Utils;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件基础中间件
/// 负责发布请求开始和结束事件，供其他插件订阅
/// 仅对代理端口的请求发布事件，跳过 Control API 端口
/// </summary>
public class BasePluginMiddleware(IPluginEventBus eventBus, IOptions<WafInfoOptions> wafInfoOptions) : IMiddleware
{
    private readonly IPluginEventBus _eventBus = eventBus;
    private readonly int _controlPort = wafInfoOptions.Value.GetControlListen().Port;
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // 跳过 Control API 端口的请求，插件不应对管理接口生效
        if (context.Connection.LocalPort == _controlPort)
        {
            await next(context);
            return;
        }

        var clientIp = RequestUtil.GetClientIp(context.Request);
        var sw = Stopwatch.StartNew();
        var startTime = DateTime.UtcNow;

        // 跟踪活跃连接
        ConnectionTracker.OnRequestStart(clientIp);

        // 发布请求开始事件
        await _eventBus.PublishAsync(new RequestStartedEvent { Context = context, VisitTime = startTime });

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            ConnectionTracker.OnRequestEnd(clientIp);

            // 发布请求完成事件
            await _eventBus.PublishAsync(new RequestCompletedEvent
            {
                Context = context,
                StatusCode = context.Response.StatusCode,
                VisitTime = startTime,
                Duration = sw.Elapsed
            });
        }
    }
}
