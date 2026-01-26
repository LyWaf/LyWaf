using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace LyWaf.Plugins.Examples;

/// <summary>
/// 示例插件：请求日志记录器
/// 演示如何创建一个完整的 LyWaf 插件
/// </summary>
public class RequestLoggerPlugin : LyWafPluginBase
{
    private RequestLoggerOptions _options = new();
    private IDisposable? _eventSubscriptionStart;
    private IDisposable? _eventSubscriptionComplete;

    public override PluginMetadata Metadata => new()
    {
        Id = "request-logger",
        Name = "请求日志记录器",
        Version = "1.0.0",
        Description = "记录所有 HTTP 请求的详细信息",
        Author = "LyWaf Team",
        Priority = PluginPriority.High,  // 高优先级，尽早记录
        EnabledByDefault = true
    };

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // 注册配置
        services.Configure<RequestLoggerOptions>(configuration.GetSection("Plugins:request-logger"));
    }

    public override Task InitializeAsync(IPluginContext context)
    {
        // 获取插件配置
        _options = context.GetPluginConfig<RequestLoggerOptions>();

        // 订阅请求完成事件（来自其他插件）
        _eventSubscriptionStart = context.SubscribeEvent<RequestStartedEvent>(async e =>
        {
            context.Logger.Info("请求开始 [事件]: {Method} {Path})",
                e.Context.Request.Method,
                e.Context.Request.Path);
            await Task.CompletedTask;
        });

        // 订阅请求完成事件（来自其他插件）
        _eventSubscriptionComplete = context.SubscribeEvent<RequestCompletedEvent>(async e =>
        {
            context.Logger.Info("请求完成 [事件]: {Method} {Path} -> {StatusCode} ({Duration}ms)",
                e.Context.Request.Method,
                e.Context.Request.Path,
                e.StatusCode,
                e.Duration.TotalMilliseconds);
            await Task.CompletedTask;
        });

        context.Logger.Info("请求日志记录器已初始化");
        return base.InitializeAsync(context);
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSubscriptionStart?.Dispose();
        _eventSubscriptionComplete?.Dispose();
        Context?.Logger.Info("请求日志记录器已停止");
        return base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// 请求日志记录器配置
/// </summary>
public class RequestLoggerOptions
{
    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否记录请求头</summary>
    public bool LogHeaders { get; set; } = false;

    /// <summary>是否记录查询字符串</summary>
    public bool LogQueryString { get; set; } = true;

    /// <summary>是否记录响应时间</summary>
    public bool LogDuration { get; set; } = true;

    /// <summary>是否通过事件总线记录</summary>
    public bool LogToEvent { get; set; } = false;

    /// <summary>忽略的路径前缀</summary>
    public List<string> IgnorePaths { get; set; } = ["/health", "/favicon.ico"];
}

