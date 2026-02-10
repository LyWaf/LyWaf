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
        // 确保 PluginOptions 已注册（Configure 可重复调用，幂等）
        services.Configure<PluginOptions>(configuration.GetSection("Plugins"));

        // 注册事件总线
        var eventBus = new PluginEventBus();
        services.AddSingleton<IPluginEventBus>(eventBus);

        // 构建临时 ServiceProvider 以获取 IOptionsMonitor<PluginOptions>
        // 这样 PluginManager 构造函数可以直接用标准 IOptionsMonitor
        var tempProvider = services.BuildServiceProvider();
        var optionsMonitor = tempProvider.GetRequiredService<IOptionsMonitor<PluginOptions>>();

        // 创建插件管理器
        var manager = new PluginManager(configuration, eventBus, optionsMonitor);
        manager.DiscoverPlugins();
        manager.ConfigureServices(services);

        // 注册为单例
        services.AddSingleton<PluginManager>(manager);
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
