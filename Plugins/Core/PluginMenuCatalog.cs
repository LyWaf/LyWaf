namespace LyWaf.Plugins.Core;

public sealed record PluginMenuEntry(string PluginId, string Id, string Name, string Path);
public sealed record PluginMenusResponse(bool Success, PluginMenuEntry[] Menus);

/// <summary>Builds console navigation from active plugins' registered page actions.</summary>
public static class PluginMenuCatalog
{
    public static IEnumerable<PluginMenuEntry> GetEntries(ILyWafPlugin plugin, PluginState state)
    {
        if (state is not (PluginState.Running or PluginState.Initialized)) yield break;
        var prefix = $"/plugins/{plugin.Metadata.Id}";
        foreach (var action in plugin.GetActions())
        {
            if (action.Type != "page-link" || string.IsNullOrWhiteSpace(action.Name) || action.Path is not { } path)
                continue;
            if (path != prefix && !path.StartsWith(prefix + "/", StringComparison.Ordinal)) continue;
            if (path.Contains('\\') || path.Contains('?') || path.Contains('#') || path.Contains('%') ||
                path.Split('/').Any(segment => segment is "." or "..")) continue;
            yield return new(plugin.Metadata.Id, action.Id, action.Name, path);
        }
    }
}
