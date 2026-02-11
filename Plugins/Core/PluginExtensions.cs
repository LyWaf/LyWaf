using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件系统扩展方法
/// </summary>
public static class PluginExtensions
{
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
