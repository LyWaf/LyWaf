using Microsoft.AspNetCore.Builder;

namespace LyWaf.Plugins.Core;

/// <summary>Defines the host services exposed to a plugin instance.</summary>
public interface IPluginContext
{
    IServiceProvider Services { get; }
    IConfiguration Configuration { get; }
    NLog.Logger Logger { get; }
    string DataDirectory { get; }
    IPluginTaskCoordinator Tasks { get; }
    IPluginStorage Storage { get; }
    IPluginNetworkClientFactory Network => throw new NotSupportedException("插件上下文未提供网络客户端。");
    T GetPluginConfig<T>(string? sectionName = null) where T : class, new();
    ILyWafPlugin? GetPlugin(string pluginId);
    IReadOnlyList<ILyWafPlugin> GetAllPlugins();
    Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : class;
    IDisposable SubscribeEvent<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
}

/// <summary>Manages plugin discovery, lifecycle, middleware, and permissions.</summary>
public interface IPluginManager
{
    ILyWafPlugin? GetPlugin(string pluginId);
    IReadOnlyList<ILyWafPlugin> GetAllPlugins();
    IReadOnlyList<ILyWafPlugin> GetSystemPlugins();
    bool IsSystemPlugin(string pluginId);
    Task EnablePluginAsync(string pluginId);
    Task DisablePluginAsync(string pluginId);
    Task ReloadPluginAsync(string pluginId);
    PluginState GetPluginState(string pluginId);
    bool IsPermissionGranted(string pluginId, PluginPermission permission) => false;
}

/// <summary>Provides streaming access limited to a plugin's data directory.</summary>
public interface IPluginStorage
{
    Stream OpenRead(string relativePath);
    Stream OpenWrite(string relativePath, bool overwrite = true);
    void Delete(string relativePath);
    bool Exists(string relativePath);
}

/// <summary>Creates host-managed HTTP clients for plugins with network permission.</summary>
public interface IPluginNetworkClientFactory
{
    HttpClient CreateClient();
}

/// <summary>Coordinates idempotent background work started by plugins.</summary>
public interface IPluginTaskCoordinator
{
    bool TryStart(string pluginId, string idempotencyKey, TimeSpan idempotencyTtl, out PluginTaskState state);
    PluginTaskState Complete(string pluginId, string idempotencyKey, bool success, string? error = null);
    bool TryGet(string pluginId, string idempotencyKey, out PluginTaskState? state);
}

/// <summary>Describes the current state of one idempotent plugin task.</summary>
public sealed record PluginTaskState(
    string PluginId,
    string IdempotencyKey,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    string? Error = null);
