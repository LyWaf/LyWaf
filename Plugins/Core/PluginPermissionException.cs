namespace LyWaf.Plugins.Core;

/// <summary>Expected activation rejection for capabilities not granted by the host configuration.</summary>
public sealed class PluginPermissionException(string pluginId, PluginPermission missing)
    : UnauthorizedAccessException($"插件 {pluginId} 缺少宿主权限：{missing}。请在 config.ly 的 Plugins.Permissions 中为 {pluginId} 配置这些权限后重试。")
{
    public string PluginId { get; } = pluginId;
    public PluginPermission MissingPermissions { get; } = missing;
}
