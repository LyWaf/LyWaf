using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件优先级
/// </summary>
public enum PluginPriority
{
    /// <summary>最高优先级（如安全相关）</summary>
    Highest = 0,
    /// <summary>高优先级</summary>
    High = 100,
    /// <summary>正常优先级</summary>
    Normal = 500,
    /// <summary>低优先级</summary>
    Low = 900,
    /// <summary>最低优先级</summary>
    Lowest = 1000
}

/// <summary>
/// 插件状态
/// </summary>
public enum PluginState
{
    /// <summary>未加载</summary>
    Unloaded,
    /// <summary>已加载</summary>
    Loaded,
    /// <summary>已初始化</summary>
    Initialized,
    /// <summary>运行中</summary>
    Running,
    /// <summary>已停止</summary>
    Stopped,
    /// <summary>错误</summary>
    Error
}

/// <summary>
/// 插件元数据
/// </summary>
public class PluginMetadata
{
    /// <summary>插件唯一标识</summary>
    public required string Id { get; init; }
    
    /// <summary>插件名称</summary>
    public required string Name { get; init; }
    
    /// <summary>插件版本</summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>插件描述</summary>
    public string Description { get; init; } = "";
    
    /// <summary>作者</summary>
    public string Author { get; init; } = "";
    
    /// <summary>插件优先级</summary>
    public PluginPriority Priority { get; init; } = PluginPriority.Normal;
    
    /// <summary>依赖的其他插件 ID 列表</summary>
    public string[] Dependencies { get; init; } = [];
    
    /// <summary>是否默认启用</summary>
    public bool EnabledByDefault { get; init; } = true;
}

/// <summary>
/// LyWaf 插件基础接口
/// 所有插件必须实现此接口
/// </summary>
public interface ILyWafPlugin
{
    /// <summary>
    /// 获取插件元数据
    /// </summary>
    PluginMetadata Metadata { get; }
    
    /// <summary>
    /// 当前插件状态
    /// </summary>
    PluginState State { get; }
    
    /// <summary>
    /// 配置服务（在 DI 容器构建前调用）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
    
    /// <summary>
    /// 初始化插件（在 DI 容器构建后调用）
    /// </summary>
    /// <param name="context">插件上下文</param>
    Task InitializeAsync(IPluginContext context);
    
    /// <summary>
    /// 配置中间件管道
    /// </summary>
    /// <param name="app">应用程序构建器</param>
    void ConfigureMiddleware(IApplicationBuilder app);
    
    /// <summary>
    /// 配置 YARP 反向代理管道（在 MapReverseProxy 内调用）
    /// </summary>
    /// <param name="proxyApp">代理应用程序构建器</param>
    void ConfigureProxyPipeline(IApplicationBuilder proxyApp);
    
    /// <summary>
    /// 启动插件
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// 停止插件
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 插件基类，提供默认实现
/// </summary>
public abstract class LyWafPluginBase : ILyWafPlugin
{
    public abstract PluginMetadata Metadata { get; }
    
    public PluginState State { get; protected set; } = PluginState.Unloaded;
    
    protected IPluginContext? Context { get; private set; }
    
    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // 默认不注册任何服务
    }
    
    public virtual Task InitializeAsync(IPluginContext context)
    {
        Context = context;
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }
    
    public virtual void ConfigureMiddleware(IApplicationBuilder app)
    {
        // 默认不配置中间件
    }
    
    public virtual void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        // 默认不配置代理管道
    }
    
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        State = PluginState.Running;
        return Task.CompletedTask;
    }
    
    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        State = PluginState.Stopped;
        return Task.CompletedTask;
    }
}
