using System.Text.Json;
using System.Text.Json.Nodes;
using LyWaf.Config;
using LyWaf.Services.Configuration;
using LyWaf.Shared;

namespace LyWaf.Control.Infrastructure;

/// <summary>为控制台业务模块提供版本化、原子化且可回滚的顶级配置补丁写入。</summary>
public sealed class ControlConfigurationPatchService(
    IConfiguration configuration,
    ConfigVersionService versions,
    ConfigurationLifecycleService lifecycle)
{
    private static readonly SemaphoreSlim Gate = ConfigurationMutationGate.Patch;

    public async Task<ConfigurationChangeImpact> SetSectionAsync<T>(string section, T value, string action,
        CancellationToken cancellationToken = default)
        => await SetPathAsync([section], value, action, cancellationToken);

    public async Task<ConfigurationChangeImpact> SetPathAsync<T>(IReadOnlyList<string> pathSegments, T value, string action,
        CancellationToken cancellationToken = default)
        => await MutateAsync(pathSegments, target =>
            target.Parent![target.Name!] = JsonSerializer.SerializeToNode(value, JsonOptions()), action, cancellationToken);

    public async Task<ConfigurationChangeImpact> MergePathAsync(IReadOnlyList<string> pathSegments,
        IReadOnlyDictionary<string, object?> values, string action, CancellationToken cancellationToken = default)
        => await MutateAsync(pathSegments, target =>
        {
            var section = target.Node as JsonObject;
            if (section == null) target.Parent![target.Name!] = section = new JsonObject();
            foreach (var (key, value) in values) section[key] = JsonSerializer.SerializeToNode(value, JsonOptions());
        }, action, cancellationToken);

    private async Task<ConfigurationChangeImpact> MutateAsync(IReadOnlyList<string> pathSegments,
        Action<JsonTarget> mutation, string action, CancellationToken cancellationToken)
    {
        if (pathSegments.Count == 0 || pathSegments.Any(x => string.IsNullOrWhiteSpace(x)))
            throw new ArgumentException("配置路径不能为空", nameof(pathSegments));
        if (configuration is not IConfigurationRoot root)
            throw new InvalidOperationException("当前配置不支持动态重载");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var before = lifecycle.Capture(root);
            var path = LbPatchConfig.GetPatchFilePath();
            var document = await ReadPatchAsync(path, cancellationToken);
            var parent = document;
            foreach (var segment in pathSegments.Take(pathSegments.Count - 1))
            {
                if (parent[segment] is not JsonObject child) parent[segment] = child = new JsonObject();
                parent = child;
            }
            var name = pathSegments[^1];
            mutation(new JsonTarget(parent, name, parent[name]));
            document["_source"] = SourceMetadata();
            var version = await versions.WritePatchAsync(path, document.ToJsonString(JsonOptions()), action, cancellationToken);
            try
            {
                return lifecycle.ReloadAndValidate(root, before);
            }
            catch
            {
                // Recovery must finish even when the caller has disconnected.
                if (version.PreviousVersionId != null) await versions.RollbackAsync(version.PreviousVersionId, CancellationToken.None);
                else File.Delete(path);
                root.Reload();
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    private sealed record JsonTarget(JsonObject Parent, string Name, JsonNode? Node);

    private static async Task<JsonObject> ReadPatchAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static JsonObject SourceMetadata()
    {
        var source = Path.GetFullPath(SharedData.ConfigFilePath);
        return new JsonObject
        {
            ["file"] = Path.GetFileName(source),
            ["lastModified"] = File.Exists(source) ? File.GetLastWriteTimeUtc(source).ToString("O") : DateTime.UtcNow.ToString("O"),
        };
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
}
