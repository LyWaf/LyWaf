using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// 插件默认配置选项实例
    /// 设置后前端可读取默认属性并提供编辑界面
    /// 例如: DefaultOptions = new ImageProcessOptions()
    /// </summary>
    public object? DefaultOptions { get; init; }
}

/// <summary>
/// 插件操作定义
/// 用于声明插件支持的可操作功能（如查看日志、清空缓存等）
/// </summary>
public class PluginAction
{
    /// <summary>操作唯一标识（如 "view-logs"）</summary>
    public required string Id { get; init; }

    /// <summary>操作显示名称（如 "查看日志"）</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 操作类型，前端根据类型渲染不同 UI
    /// "log-viewer" = 日志查看器（分页、搜索、展开详情）
    /// "button" = 一次性执行按钮（如清空缓存）
    /// </summary>
    public string Type { get; init; } = "button";

    /// <summary>操作说明</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 插件操作执行结果
/// </summary>
public class PluginActionResult
{
    public bool Success { get; set; } = true;
    public string? Message { get; set; }
    /// <summary>JSON 可序列化的数据（具体结构取决于操作类型）</summary>
    public object? Data { get; set; }
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

    /// <summary>
    /// 获取插件支持的操作列表
    /// 返回空列表表示无自定义操作
    /// </summary>
    IReadOnlyList<PluginAction> GetActions() => [];

    /// <summary>
    /// 执行插件操作
    /// </summary>
    /// <param name="actionId">操作 ID</param>
    /// <param name="parameters">操作参数</param>
    Task<PluginActionResult> ExecuteActionAsync(string actionId, Dictionary<string, string>? parameters)
        => Task.FromResult(new PluginActionResult { Success = false, Message = $"操作 {actionId} 未实现" });
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
        // 如果设置了 DefaultOptions，自动注册 services.Configure<T>(section)
        if (Metadata.DefaultOptions is { } options)
        {
            var section = configuration.GetSection($"Plugins:{Metadata.Id}");
            // 反射调用 services.Configure<T>(IConfigurationSection)
            typeof(OptionsConfigurationServiceCollectionExtensions)
                .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                .First(m => m.Name == "Configure" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(IConfiguration))
                .MakeGenericMethod(options.GetType())
                .Invoke(null, [services, section]);
        }
    }

    public virtual Task InitializeAsync(IPluginContext context)
    {
        Context = context;
        // 自动将配置绑定到 DefaultOptions 实例（清空 List/Dictionary 后再 Bind，避免重复）
        BindOptionsFromConfig(context.Configuration);
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 将配置绑定到 Metadata.DefaultOptions 实例上
    /// 自动清空 List/Dictionary 属性，避免 Bind() 追加导致重复
    /// </summary>
    private void BindOptionsFromConfig(IConfiguration configuration)
    {
        var options = Metadata.DefaultOptions;
        if (options == null) return;
        // Bind() 对 List/Dictionary 是追加操作，必须先清空
        foreach (var prop in options.GetType().GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            object? value;
            try { value = prop.GetValue(options); } catch { continue; }
            if (value is System.Collections.IList list) list.Clear();
            else if (value is System.Collections.IDictionary dict) dict.Clear();
        }
        configuration.GetSection($"Plugins:{Metadata.Id}").Bind(options);
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

    /// <summary>
    /// 获取插件支持的操作列表（子类可重写）
    /// </summary>
    public virtual IReadOnlyList<PluginAction> GetActions() => [];

    /// <summary>
    /// 执行插件操作（子类可重写）
    /// </summary>
    public virtual Task<PluginActionResult> ExecuteActionAsync(string actionId, Dictionary<string, string>? parameters)
        => Task.FromResult(new PluginActionResult { Success = false, Message = $"操作 {actionId} 未实现" });
}
