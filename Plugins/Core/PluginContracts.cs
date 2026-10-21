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
    IPluginNodeMessageClient NodeMessages => throw new NotSupportedException("Host node messaging is unavailable.");
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

/// <summary>Exact schema versions, not a dynamic schema download or executable-code contract.</summary>
public sealed record PluginNodeMessageCapability(string MessageType, int SchemaVersion,
    IReadOnlyList<string> TargetGroups, bool CanSend = false, bool CanReceive = false);

/// <summary>Bound to the current plugin by the host; no destination URI, peer credentials, or arbitrary plugin identity.</summary>
public interface IPluginNodeMessageClient
{
    Task<IReadOnlyList<PluginNodeDelivery>> SendAsync(string messageType, int schemaVersion, string targetGroup,
        System.Text.Json.JsonElement payload, string messageId, CancellationToken cancellationToken);
}

public sealed record PluginNodeDelivery(string NodeId, string MessageId, bool Ack, string? ErrorCode = null);
public sealed record PluginNodeReceivedMessage(string MessageId, string SenderNodeId, string MessageType,
    int SchemaVersion, string TargetGroup, System.Text.Json.JsonElement Payload);
public sealed record PluginNodeHandlerResult(bool Ack, string? ErrorCode = null);

/// <summary>Handle only declared, host-authorized messages. Exceptions leave an unknown outcome, never automatic replay.</summary>
public interface IPluginNodeMessageHandler
{
    Task<PluginNodeHandlerResult> HandleNodeMessageAsync(PluginNodeReceivedMessage message, CancellationToken cancellationToken);
}

/// <summary>可选通知接收能力，宿主仅调用处于运行状态并已授权网络权限的插件。</summary>
public interface IPluginNotificationSink
{
    Task SendNotificationAsync(PluginNotification notification, CancellationToken cancellationToken);
}

/// <summary>不包含认证凭据及请求原文的聚合告警。</summary>
public sealed record PluginNotification(string Id, string Site, string RuleId, string AttackType, string Ip, long Count, DateTimeOffset LastSeen);

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
