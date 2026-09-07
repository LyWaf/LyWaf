namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件事件总线接口
/// 用于插件间的松耦合通信
/// </summary>
public interface IPluginEventBus
{
    /// <summary>
    /// 发布事件
    /// </summary>
    Task PublishAsync<TEvent>(TEvent @event) where TEvent : class;
    
    /// <summary>
    /// 订阅事件
    /// </summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
}

/// <summary>
/// 内置事件：请求开始
/// </summary>
public class RequestStartedEvent
{
    public required HttpContext Context { get; init; }
    public DateTime VisitTime { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 内置事件：请求结束
/// </summary>
public class RequestCompletedEvent
{
    public required HttpContext Context { get; init; }
    public DateTime VisitTime { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; init; }
    public int StatusCode { get; init; }
}

/// <summary>
/// 内置事件：配置变更
/// </summary>
public class ConfigurationChangedEvent
{
    public required string SectionName { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 内置事件：插件状态变更
/// </summary>
public class PluginStateChangedEvent
{
    public required string PluginId { get; init; }
    public required PluginState OldState { get; init; }
    public required PluginState NewState { get; init; }
}

/// <summary>描述插件初始化、启动、停止或卸载操作的结果，供指标与审计订阅者消费。</summary>
public class PluginOperationEvent
{
    public required string PluginId { get; init; }
    public required string Operation { get; init; }
    public required bool Success { get; init; }
}
