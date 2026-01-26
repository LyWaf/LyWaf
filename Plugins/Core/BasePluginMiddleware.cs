using System.Diagnostics;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件基础中间件
/// 负责发布请求开始和结束事件，供其他插件订阅
/// </summary>
public class BasePluginMiddleware : IMiddleware
{
    private readonly IPluginEventBus _eventBus;
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    public BasePluginMiddleware(IPluginEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();

        // 发布请求开始事件
        await _eventBus.PublishAsync(new RequestStartedEvent { Context = context });

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();

            // 发布请求完成事件
            await _eventBus.PublishAsync(new RequestCompletedEvent
            {
                Context = context,
                StatusCode = context.Response.StatusCode,
                Duration = sw.Elapsed
            });
        }
    }
}
