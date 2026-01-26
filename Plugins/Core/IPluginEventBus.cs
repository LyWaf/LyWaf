using System.Collections.Concurrent;

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
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 内置事件：请求结束
/// </summary>
public class RequestCompletedEvent
{
    public required HttpContext Context { get; init; }
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

/// <summary>
/// 插件事件总线实现
/// </summary>
public class PluginEventBus : IPluginEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
    
    public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : class
    {
        var eventType = typeof(TEvent);
        
        if (!_handlers.TryGetValue(eventType, out var handlers))
        {
            return;
        }
        
        List<Delegate> handlersCopy;
        lock (_lock)
        {
            handlersCopy = [.. handlers];
        }
        
        foreach (var handler in handlersCopy)
        {
            try
            {
                if (handler is Func<TEvent, Task> typedHandler)
                {
                    await typedHandler(@event);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "事件处理器执行失败: {EventType}", eventType.Name);
            }
        }
    }
    
    public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        var eventType = typeof(TEvent);
        
        lock (_lock)
        {
            if (!_handlers.TryGetValue(eventType, out var handlers))
            {
                handlers = [];
                _handlers[eventType] = handlers;
            }
            handlers.Add(handler);
        }
        
        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(eventType, out var handlers))
                {
                    handlers.Remove(handler);
                }
            }
        });
    }
    
    private class Subscription(Action unsubscribe) : IDisposable
    {
        private readonly Action _unsubscribe = unsubscribe;
        private bool _disposed;
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _unsubscribe();
                _disposed = true;
            }
        }
    }
}
