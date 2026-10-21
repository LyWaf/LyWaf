using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodePersistentIdentity(string ClusterId, string NodeId);
public sealed record NodeControlCapabilities(int AuthorityBootstrap, int SessionEnd,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int CcMembership = 0);
public sealed record NodeHandshake(string ClusterId, string NodeId, string BootId, string Role,
    DateTimeOffset StartedAt, string ApplicationVersion, IReadOnlyList<int> ProtocolVersions,
    NodeConfigurationRevision? Configuration = null, NodePluginCapabilitySummary? Plugins = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Ending = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] NodeControlCapabilities? ControlCapabilities = null);

/// <summary>Owns a durable local identity and an exclusive lease for the lifetime of this host.</summary>
public sealed class NodeIdentityService(IOptionsMonitor<NodeCommunicationOptions> options) : IHostedService, IDisposable
{
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private NodePersistentIdentity? _identity;
    private FileStream? _lease;
    private string? _directory;

    internal static NodePersistentIdentity Resolve(NodeCommunicationOptions config)
    {
        if (!NodeMessageAuthentication.ValidId(config.ClusterId)
            || !string.IsNullOrEmpty(config.NodeId) && !NodeMessageAuthentication.ValidId(config.NodeId))
            throw new InvalidOperationException("Invalid persistent node identity");
        var directory = Path.GetFullPath(config.StateDirectory);
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        var path = Path.Combine(directory, "identity.json");
        var lockPath = Path.Combine(directory, "identity.lock");
        RejectLink(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RejectLink(path);
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4096) throw new InvalidDataException("Invalid node identity file");
            var identity = JsonSerializer.Deserialize<NodePersistentIdentity>(stream);
            if (identity is null || !NodeMessageAuthentication.ValidId(identity.NodeId)
                || identity.ClusterId != config.ClusterId
                || !string.IsNullOrEmpty(config.NodeId) && identity.NodeId != config.NodeId)
                throw new InvalidDataException("Persisted node identity conflicts with configuration");
            return identity;
        }
        var created = new NodePersistentIdentity(config.ClusterId,
            string.IsNullOrEmpty(config.NodeId) ? "node-" + Guid.NewGuid().ToString("N") : config.NodeId);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, created);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return created;
    }

    public Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = options.CurrentValue;
        if (!config.Enabled) return Task.CompletedTask;
        if (_lease is not null) throw new InvalidOperationException("Node identity is already active");
        var directory = Path.GetFullPath(config.StateDirectory);
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        var path = Path.Combine(directory, "identity-owner.lock");
        RejectLink(path);
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _identity = Resolve(config);
            _directory = directory;
            _lease = lease;
        }
        catch { lease.Dispose(); throw; }
        return Task.CompletedTask;
    }

    public NodeHandshake Capture()
    {
        var config = options.CurrentValue;
        if (_lease is null || _identity is null || !config.Enabled || config.ClusterId != _identity.ClusterId
            || config.NodeId != _identity.NodeId || Path.GetFullPath(config.StateDirectory) != _directory)
            throw new InvalidOperationException("Node identity is unavailable or changed; restart with a valid configuration");
        return new(_identity.ClusterId, _identity.NodeId, _bootId, config.Role, _startedAt,
            typeof(NodeIdentityService).Assembly.GetName().Version?.ToString() ?? "unknown", [2]);
    }

    public Task StopAsync(CancellationToken token) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { _lease?.Dispose(); _lease = null; }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Node identity paths cannot be symbolic links");
    }
}
