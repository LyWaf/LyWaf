using LyWaf.Services.RuntimeState;

namespace LyWaf.Plugins.Core;

/// <summary>基于统一运行状态层实现插件任务幂等和状态传播。</summary>
public sealed class PluginTaskCoordinator(IRuntimeStateStore stateStore) : IPluginTaskCoordinator
{
    private const string ClaimDomain = "plugin.idempotency";
    private const string TaskDomain = "plugin.task";

    public bool TryStart(string pluginId, string idempotencyKey, TimeSpan idempotencyTtl, out PluginTaskState state)
    {
        Validate(pluginId, idempotencyKey);
        var key = Key(pluginId, idempotencyKey);
        if (!stateStore.TryClaim(ClaimDomain, key, idempotencyTtl))
        {
            if (!stateStore.TryGet(TaskDomain, key, out PluginTaskState? existing))
                existing = new(pluginId, idempotencyKey, "claimed", DateTimeOffset.UtcNow);
            state = existing!;
            return false;
        }
        state = new(pluginId, idempotencyKey, "running", DateTimeOffset.UtcNow);
        stateStore.Set(TaskDomain, key, state, idempotencyTtl);
        return true;
    }

    public PluginTaskState Complete(string pluginId, string idempotencyKey, bool success, string? error = null)
    {
        Validate(pluginId, idempotencyKey);
        var key = Key(pluginId, idempotencyKey);
        stateStore.TryGet(TaskDomain, key, out PluginTaskState? current);
        var completed = new PluginTaskState(pluginId, idempotencyKey, success ? "succeeded" : "failed",
            current?.StartedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, error);
        stateStore.Set(TaskDomain, key, completed, TimeSpan.FromDays(1));
        return completed;
    }

    public bool TryGet(string pluginId, string idempotencyKey, out PluginTaskState? state)
        => stateStore.TryGet(TaskDomain, Key(pluginId, idempotencyKey), out state);

    private static string Key(string pluginId, string idempotencyKey) => pluginId + ":" + idempotencyKey;
    private static void Validate(string pluginId, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 128) throw new ArgumentException("插件 ID 无效。");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 512) throw new ArgumentException("幂等键无效。");
    }
}
