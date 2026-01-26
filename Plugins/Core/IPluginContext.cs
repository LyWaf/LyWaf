using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件上下文接口
/// 提供插件与主程序交互的能力
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// 服务提供者（用于解析服务）
    /// </summary>
    IServiceProvider Services { get; }
    
    /// <summary>
    /// 配置（只读）
    /// </summary>
    IConfiguration Configuration { get; }
    
    /// <summary>
    /// 获取插件专属配置节
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="sectionName">配置节名称（默认使用插件 ID）</param>
    T GetPluginConfig<T>(string? sectionName = null) where T : class, new();
    
    /// <summary>
    /// 日志记录器
    /// </summary>
    NLog.Logger Logger { get; }
    
    /// <summary>
    /// 插件数据目录（用于存储插件数据）
    /// </summary>
    string DataDirectory { get; }
    
    /// <summary>
    /// 获取其他已加载的插件
    /// </summary>
    /// <param name="pluginId">插件 ID</param>
    ILyWafPlugin? GetPlugin(string pluginId);
    
    /// <summary>
    /// 获取所有已加载的插件
    /// </summary>
    IReadOnlyList<ILyWafPlugin> GetAllPlugins();
    
    /// <summary>
    /// 发布事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件数据</param>
    Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : class;
    
    /// <summary>
    /// 订阅事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">事件处理器</param>
    IDisposable SubscribeEvent<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
}

/// <summary>
/// 插件上下文实现
/// </summary>
public class PluginContext : IPluginContext
{
    private readonly IPluginManager _pluginManager;
    private readonly string _pluginId;
    private readonly IPluginEventBus _eventBus;
    
    public IServiceProvider Services { get; }
    public IConfiguration Configuration { get; }
    public NLog.Logger Logger { get; }
    public string DataDirectory { get; }
    
    public PluginContext(
        string pluginId,
        IServiceProvider services,
        IConfiguration configuration,
        IPluginManager pluginManager,
        IPluginEventBus eventBus,
        string baseDataDirectory)
    {
        _pluginId = pluginId;
        _pluginManager = pluginManager;
        _eventBus = eventBus;
        Services = services;
        Configuration = configuration;
        Logger = NLog.LogManager.GetLogger($"Plugin.{pluginId}");
        DataDirectory = Path.Combine(baseDataDirectory, pluginId);
        
        // 确保数据目录存在
        if (!Directory.Exists(DataDirectory))
        {
            Directory.CreateDirectory(DataDirectory);
        }
    }
    
    public T GetPluginConfig<T>(string? sectionName = null) where T : class, new()
    {
        var section = sectionName ?? $"Plugins:{_pluginId}";
        var config = new T();
        Configuration.GetSection(section).Bind(config);
        return config;
    }
    
    public ILyWafPlugin? GetPlugin(string pluginId)
    {
        return _pluginManager.GetPlugin(pluginId);
    }
    
    public IReadOnlyList<ILyWafPlugin> GetAllPlugins()
    {
        return _pluginManager.GetAllPlugins();
    }
    
    public Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : class
    {
        return _eventBus.PublishAsync(@event);
    }
    
    public IDisposable SubscribeEvent<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        return _eventBus.Subscribe(handler);
    }
}
