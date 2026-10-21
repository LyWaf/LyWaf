using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Shared;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeConfigurationRevision(string? AppliedVersionId, string MainFileSha256, string? MainFileVersionId);
public sealed record NodePluginCapability(string PluginId, string Version, bool Running,
    IReadOnlyList<PluginNodeMessageCapability> Messages);
public sealed record NodePluginCapabilitySummary(string Digest, IReadOnlyList<NodePluginCapability> Items);

public interface INodeHandshakeConfigurationSource
{
    Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token);
}

/// <summary>Separates last applied deployment revision from the current file, which may await reload.</summary>
public sealed class NodeHandshakeConfigurationSource(ConfigVersionService versions, DistributionNodeService node)
    : INodeHandshakeConfigurationSource
{
    public async Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token)
    {
        var path = Path.GetFullPath(SharedData.ConfigFilePath);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds handshake read limit");
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        var known = (await versions.GetVersionsAsync(token)).FirstOrDefault(v => v.Kind == "main"
            && Path.GetFullPath(v.TargetPath).Equals(path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && v.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase));
        return new(node.GetStatus().ConfigVersionId, digest, known?.Id);
    }
}

public sealed class NodeHandshakeMetadataService(NodeIdentityService identity, INodeHandshakeConfigurationSource configuration,
    IPluginManager plugins, IOptionsMonitor<PluginNodeMessageOptions> messaging)
{
    public async Task<NodeHandshake> CaptureAsync(CancellationToken token)
    {
        var local = identity.Capture();
        var config = await configuration.CaptureAsync(token);
        var policy = messaging.CurrentValue;
        var items = plugins.GetAllPlugins().Select(plugin =>
        {
            var meta = plugin.Metadata;
            var running = plugins.GetPluginState(meta.Id) == PluginState.Running;
            var messages = new List<PluginNodeMessageCapability>();
            if (running && policy.Enabled && plugins.IsPermissionGranted(meta.Id, PluginPermission.NodeMessaging)
                && policy.Grants.TryGetValue(meta.Id, out var grant))
                foreach (var declared in meta.NodeMessages)
                {
                    foreach (var group in declared.TargetGroups.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    {
                        if (!policy.Groups.ContainsKey(group)) continue;
                        var send = declared.CanSend && grant.SendGroups.Contains(group, StringComparer.Ordinal);
                        var receive = declared.CanReceive && plugin is IPluginNodeMessageHandler
                            && grant.ReceiveGroups.Contains(group, StringComparer.Ordinal);
                        if (send || receive) messages.Add(new(declared.MessageType, declared.SchemaVersion, [group], send, receive));
                    }
                }
            return new NodePluginCapability(meta.Id, meta.Version, running, messages
                .OrderBy(m => m.MessageType, StringComparer.Ordinal).ThenBy(m => m.SchemaVersion)
                .ThenBy(m => m.TargetGroups[0], StringComparer.Ordinal).ToArray());
        }).OrderBy(p => p.PluginId, StringComparer.Ordinal).ToArray();
        var result = local with { Configuration = config, Plugins = new(ComputeDigest(items), items), ControlCapabilities = new(1, 1, 1) };
        NodeHandshakeValidation.Validate(result, DateTimeOffset.UtcNow);
        token.ThrowIfCancellationRequested();
        return result;
    }

    public static string ComputeDigest(IReadOnlyList<NodePluginCapability> items)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items)));
}

internal static class NodeHandshakeValidation
{
    internal static void Validate(NodeHandshake value, DateTimeOffset now)
    {
        static bool Text(string? text, int max) => text is { Length: > 0 } && text.Length <= max && !text.Any(char.IsControl);
        static bool Revision(string? value) => value is null || Text(value, 256);
        if (!NodeMessageAuthentication.ValidId(value.ClusterId) || !NodeMessageAuthentication.ValidId(value.NodeId)
            || !Guid.TryParseExact(value.BootId, "N", out _) || value.Role is not ("Node" or "Controller")
            || value.Ending && value.Role != "Node"
            || value.ControlCapabilities is { } capabilities && (capabilities.AuthorityBootstrap is < 0 or > 65535 || capabilities.SessionEnd is < 0 or > 65535
                || capabilities.CcMembership is < 0 or > 65535)
            || value.StartedAt == default || value.StartedAt > now.AddMinutes(5)
            || !Text(value.ApplicationVersion, 128) || value.ProtocolVersions is not { Count: > 0 and <= 8 }
            || !value.ProtocolVersions.Contains(2) || value.ProtocolVersions.Any(v => v <= 0)
            || value.ProtocolVersions.Distinct().Count() != value.ProtocolVersions.Count
            || value.Configuration is not { } config || !Revision(config.AppliedVersionId) || !Revision(config.MainFileVersionId)
            || config.MainFileSha256 is not { Length: 64 } || !config.MainFileSha256.All(Uri.IsHexDigit)
            || value.Plugins is not { Items.Count: <= 256 } summary
            || summary.Items.Any(p => p is null || !Text(p.PluginId, 128) || !Text(p.Version, 128)
                || p.Messages is not { Count: <= 256 } || p.Messages.Any(m => m is null || !Text(m.MessageType, 128)
                    || m.SchemaVersion <= 0 || m.TargetGroups is not { Count: > 0 and <= 64 }
                    || m.TargetGroups.Any(g => !NodeMessageAuthentication.ValidId(g)) || !m.CanSend && !m.CanReceive)
                || !p.Running && p.Messages.Count != 0)
            || summary.Items.Select(p => p.PluginId).Distinct(StringComparer.Ordinal).Count() != summary.Items.Count
            || summary.Digest != NodeHandshakeMetadataService.ComputeDigest(summary.Items)
            || JsonSerializer.SerializeToUtf8Bytes(value).Length > 256 * 1024)
            throw new InvalidDataException("Invalid node handshake metadata");
    }
}
