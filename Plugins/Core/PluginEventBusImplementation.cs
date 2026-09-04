using System.Collections.Concurrent;

namespace LyWaf.Plugins.Core;

/// <summary>Provides the host's thread-safe in-process implementation of the plugin event bus.</summary>
public sealed class PluginEventBus : IPluginEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _sync = new();
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task PublishAsync<TEvent>(TEvent @event) where TEvent : class
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;
        List<Delegate> snapshot;
        lock (_sync) snapshot = [.. handlers];
        foreach (var handler in snapshot)
        {
            try
            {
                if (handler is Func<TEvent, Task> typedHandler) await typedHandler(@event);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "事件处理器执行失败: {EventType}", typeof(TEvent).Name);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
                _handlers[typeof(TEvent)] = handlers = [];
            handlers.Add(handler);
        }
        return new Subscription(() =>
        {
            lock (_sync)
                if (_handlers.TryGetValue(typeof(TEvent), out var handlers)) handlers.Remove(handler);
        });
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            unsubscribe();
            _disposed = true;
        }
    }
}
