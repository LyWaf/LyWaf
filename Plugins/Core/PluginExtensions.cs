using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using LyWaf.Plugins.Packaging;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件系统扩展方法
/// </summary>
public static class PluginExtensions
{
    private static readonly string[] PluginApiMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// 添加插件系统
    /// </summary>
    public static IServiceCollection AddLyWafPlugins(this IServiceCollection services, IConfiguration configuration)
    {
        // 注册 PluginOptions
        services.Configure<PluginOptions>(configuration.GetSection("Plugins"));

        // 注册事件总线
        var eventBus = new PluginEventBus();
        services.AddSingleton<IPluginEventBus>(eventBus);

        // 通过 BuildServiceProvider 获取 IOptionsMonitor，用于创建 PluginManager
        var sp = services.BuildServiceProvider();
        var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<PluginOptions>>();

        var manager = new PluginManager(configuration, eventBus, optionsMonitor);
        manager.DiscoverPlugins();
        manager.ConfigureServices(services);

        services.AddSingleton(manager);
        services.AddSingleton<IPluginManager>(manager);

        // 注册插件生命周期管理器
        services.AddHostedService<PluginHostedService>();

        // 注册插件基础中间件（用于发布请求事件）
        services.AddSingleton<BasePluginMiddleware>();
        services.AddSingleton<PluginApiAdmissionGate>();
        services.AddSingleton<IPluginTaskCoordinator, PluginTaskCoordinator>();
        services.AddSingleton<PluginPackageManager>();

        return services;
    }
    
    /// <summary>
    /// 使用插件系统
    /// </summary>
    public static IApplicationBuilder UseLyWafPlugins(this IApplicationBuilder app)
    {
        var manager = app.ApplicationServices.GetRequiredService<PluginManager>();
        manager.ConfigureMiddleware(app);
        // 注册基础中间件，发布请求开始/结束事件
        app.UseMiddleware<BasePluginMiddleware>();
        return app;
    }

    /// <summary>注册统一插件 API 代理，插件业务路由不会进入 ControlApi。</summary>
    public static WebApplication MapLyWafPluginApi(this WebApplication app, int controlPort)
    {
        app.MapMethods("/api/plugins/{pluginId}/proxy/{**pluginPath}", PluginApiMethods,
            async (string pluginId, string? pluginPath, HttpContext context, IPluginManager manager, PluginApiAdmissionGate gate) =>
            {
                var username = context.Items["Username"]?.ToString() ?? "unknown";
                var admission = gate.TryAcquire(username, pluginId, context.Request.ContentLength);
                if (!admission.Allowed)
                {
                    if (admission.RetryAfterSeconds.HasValue) context.Response.Headers.RetryAfter = admission.RetryAfterSeconds.Value.ToString();
                    AuditProxy(context, pluginId, pluginPath, false, admission.ErrorCode!);
                    return ManagementApiContract.Error(context,
                        admission.ErrorCode == "plugin_request_too_large" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status429TooManyRequests,
                        admission.ErrorCode!, admission.Message!, new { admission.RetryAfterSeconds });
                }

                using var lease = admission.Lease;
                var stopwatch = Stopwatch.StartNew();
                var outcome = "ok";
                try
                {
                var plugin = manager.GetPlugin(pluginId);
                if (plugin == null)
                {
                    outcome = "plugin_not_found";
                    return ManagementApiContract.Error(context, 404, outcome, $"插件 {pluginId} 不存在");
                }
                if (manager.GetPluginState(pluginId) is not (PluginState.Running or PluginState.Initialized))
                {
                    outcome = "plugin_disabled";
                    return ManagementApiContract.Error(context, 503, outcome, $"插件 {pluginId} 未启用");
                }
                if (plugin is not IPluginApiHandler handler)
                {
                    outcome = "plugin_api_unavailable";
                    return ManagementApiContract.Error(context, 404, outcome, $"插件 {pluginId} 未提供控制台 API");
                }
                if (!manager.IsPermissionGranted(pluginId, PluginPermission.ConsoleApi))
                {
                    outcome = "plugin_permission_denied";
                    return ManagementApiContract.Error(context, 403, outcome, $"插件 {pluginId} 未获控制台 API 权限");
                }
                return await handler.HandleApiAsync(pluginPath ?? "", context);
                }
                catch
                {
                    outcome = "unhandled_error";
                    throw;
                }
                finally
                {
                    AuditProxy(context, pluginId, pluginPath, outcome == "ok", $"{outcome}, {stopwatch.ElapsedMilliseconds}ms");
                }
            }).RequireHost($"*:{controlPort}");
        return app;
    }

    private static void AuditProxy(HttpContext context, string pluginId, string? pluginPath, bool success, string outcome)
    {
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown",
            $"插件 API {(success ? "调用" : "拒绝")}: {pluginId}/{pluginPath ?? ""} [{outcome}]",
            context.Connection.RemoteIpAddress?.ToString() ?? "");
    }
    
    /// <summary>
    /// 在代理管道中使用高优先级插件（Highest, High）
    /// 应在核心中间件之前调用
    /// </summary>
    public static IApplicationBuilder UseLyWafPluginsInProxyHigh(this IApplicationBuilder proxyApp)
    {
        var manager = proxyApp.ApplicationServices.GetRequiredService<PluginManager>();
        manager.ConfigureProxyPipelineHigh(proxyApp);
        return proxyApp;
    }
    
    /// <summary>
    /// 在代理管道中使用普通及低优先级插件（Normal, Low, Lowest）
    /// 应在核心中间件之后调用
    /// </summary>
    public static IApplicationBuilder UseLyWafPluginsInProxyNormal(this IApplicationBuilder proxyApp)
    {
        var manager = proxyApp.ApplicationServices.GetRequiredService<PluginManager>();
        manager.ConfigureProxyPipelineNormal(proxyApp);
        return proxyApp;
    }
    
    /// <summary>
    /// 初始化插件（在 app.Build() 之后调用）
    /// </summary>
    public static async Task<IHost> InitializeLyWafPluginsAsync(this IHost host)
    {
        var manager = host.Services.GetRequiredService<PluginManager>();
        await manager.InitializePluginsAsync(host.Services);
        return host;
    }
}

/// <summary>
/// 插件生命周期托管服务
/// </summary>
public class PluginHostedService(PluginManager pluginManager) : IHostedService
{
    private readonly PluginManager _pluginManager = pluginManager;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _pluginManager.StartPluginsAsync(cancellationToken);
    }
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _pluginManager.StopPluginsAsync(cancellationToken);
    }
}
