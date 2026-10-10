using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Config;
using LyWaf.Shared;
using LyWaf.Services.Configuration;

namespace LyWaf.Plugins.Core;

/// <summary>Reports publication separately from merely persisting a configuration file.</summary>
public sealed record PluginPublicationResult(bool Success, string Status, string Version, string Message);

/// <summary>Serializes console publications and validates the entire affected scope tree before writing.</summary>
public sealed class PluginConfigurationPublisher(IConfiguration configuration, PluginScopedConfiguration scopes,
    ILogger<PluginConfigurationPublisher>? logger = null)
{
    private static readonly byte[] VersionKey = RandomNumberGenerator.GetBytes(32);

    public string Version()
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, VersionKey);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(configuration.AsEnumerable().Where(p => p.Value != null)
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)));
        // Include disk changes not yet reflected by a configuration reload.
        foreach (var path in new[] { SharedData.ConfigFilePath, LbPatchConfig.GetPatchFilePath() })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { hash.AppendData([0]); continue; }
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public string? Validate(ILyWafPlugin plugin, IReadOnlyDictionary<string, string> candidate)
    {
        if (plugin.Metadata.SupportsScopedConfiguration)
        {
            if (candidate.Count == 0) throw new InvalidOperationException("完整配置不能为空");
            return scopes.ValidateGlobalCandidate(plugin.Metadata.Id, candidate);
        }
        return null;
    }

    public async Task<PluginPublicationResult> PublishAsync(ILyWafPlugin plugin, string? expectedVersion,
        IReadOnlyDictionary<string, string> candidate, Func<Action, Task> persist)
    {
        await ConfigurationMutationGate.Patch.WaitAsync();
        try
        {
            var version = Version();
            if (string.IsNullOrWhiteSpace(expectedVersion) || !string.Equals(version, expectedVersion.Trim('"'), StringComparison.Ordinal))
                return new(false, "version_conflict", version, "配置已变化或缺少版本，请重新读取后再保存");
            string? fingerprint;
            try { fingerprint = Validate(plugin, candidate); }
            catch (InvalidOperationException ex) { return new(false, "validation_failed", version, ex.Message); }
            try
            {
                await persist(() => { if (fingerprint != null) scopes.EnsureApplied(fingerprint); });
                return plugin.Metadata.SupportsScopedConfiguration
                    ? new(true, "applied", Version(), "插件配置已生效")
                    : new(true, "restart_required", Version(), "配置已保存，此插件尚未接入分层配置，请重启后确认生效");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
            {
                logger?.LogError(ex, "Plugin configuration publication failed for {PluginId}", plugin.Metadata.Id);
                return new(false, "publish_failed", version, "配置发布失败，请检查配置版本与服务日志");
            }
        }
        finally { ConfigurationMutationGate.Patch.Release(); }
    }
}
