using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Shared;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Distribution;

/// <summary>目标节点实现 prepare/commit/rollback 两阶段应用，任何失败都保留或恢复旧配置。</summary>
public sealed class DistributionNodeService(
    IOptionsMonitor<DistributionOptions> options,
    ConfigVersionService versions,
    ConfigurationLifecycleService lifecycle,
    IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private NodeRuntimeState _state = new();
    private bool _stateLoaded;

    public DistributionNodeStatus GetStatus()
    {
        EnsureStateLoaded();
        var current = options.CurrentValue;
        return new(current.NodeId, current.Role,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            _state.DeploymentId, _state.ConfigVersionId, _state.State, DateTimeOffset.UtcNow);
    }

    public async Task<NodeAcknowledgement> PrepareAsync(PrepareConfigurationRequest request, CancellationToken cancellationToken)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.DeploymentId == request.DeploymentId &&
                _state.SourceVersionId == request.VersionId &&
                _state.State is "Prepared" or "Applied")
                return new(true, options.CurrentValue.NodeId, request.DeploymentId, "prepare", _state.ConfigVersionId);
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Content))).ToLowerInvariant();
            if (!actual.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                return Nack(request, "prepare", "配置摘要不匹配");
            versions.ValidateMainCandidate(SharedData.ConfigFilePath, request.Content);
            var path = PreparedPath(request.DeploymentId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicWriteAsync(path, JsonSerializer.Serialize(request), cancellationToken);
            _state = new(request.DeploymentId, request.VersionId, null, null, "Prepared");
            await SaveStateAsync(cancellationToken);
            return Ack(request, "prepare");
        }
        catch (Exception ex) { return Nack(request, "prepare", ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<NodeAcknowledgement> CommitAsync(string deploymentId, CancellationToken cancellationToken)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.DeploymentId == deploymentId && _state.State == "Applied")
                return new(true, options.CurrentValue.NodeId, deploymentId, "commit", _state.ConfigVersionId);
            var prepared = await ReadPreparedAsync(deploymentId, cancellationToken);
            if (prepared == null) return Nack(deploymentId, "commit", "候选配置不存在");
            var root = configuration as IConfigurationRoot ?? throw new InvalidOperationException("配置根不支持重载");
            var before = lifecycle.Capture(root);
            var written = await versions.WriteMainAsync(SharedData.ConfigFilePath, prepared.Content,
                $"distribution:{deploymentId}", cancellationToken);
            try { lifecycle.ReloadAndValidate(root, before); }
            catch
            {
                if (written.PreviousVersionId != null) await versions.RollbackAsync(written.PreviousVersionId, cancellationToken);
                root.Reload();
                throw;
            }
            _state = new(deploymentId, prepared.VersionId, written.Id, written.PreviousVersionId, "Applied");
            await SaveStateAsync(cancellationToken);
            return new(true, options.CurrentValue.NodeId, deploymentId, "commit", written.Id);
        }
        catch (Exception ex) { return Nack(deploymentId, "commit", ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<NodeAcknowledgement> RollbackAsync(string deploymentId, CancellationToken cancellationToken)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.DeploymentId == deploymentId && _state.State == "RolledBack")
                return new(true, options.CurrentValue.NodeId, deploymentId, "rollback", _state.ConfigVersionId);
            if (_state.DeploymentId != deploymentId || string.IsNullOrWhiteSpace(_state.PreviousVersionId))
                return Nack(deploymentId, "rollback", "没有该任务的可回滚版本");
            var restored = await versions.RollbackAsync(_state.PreviousVersionId, cancellationToken)
                ?? throw new InvalidOperationException("回滚版本不存在");
            if (configuration is IConfigurationRoot root)
            {
                root.Reload();
                lifecycle.Validate(root);
            }
            _state = _state with { ConfigVersionId = restored.Id, State = "RolledBack" };
            await SaveStateAsync(cancellationToken);
            return new(true, options.CurrentValue.NodeId, deploymentId, "rollback", restored.Id);
        }
        catch (Exception ex) { return Nack(deploymentId, "rollback", ex.Message); }
        finally { _gate.Release(); }
    }

    private async Task<PrepareConfigurationRequest?> ReadPreparedAsync(string deploymentId, CancellationToken token)
    {
        var path = PreparedPath(deploymentId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PrepareConfigurationRequest>(stream, cancellationToken: token);
    }

    private string PreparedPath(string deploymentId)
    {
        if (deploymentId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("DeploymentId 无效");
        return Path.Combine(StateDirectory(), "prepared", deploymentId + ".json");
    }

    private string StateDirectory() => Path.GetFullPath(options.CurrentValue.StateDirectory);

    private void EnsureStateLoaded()
    {
        if (_stateLoaded) return;
        lock (_loadLock)
        {
            if (_stateLoaded) return;
            var path = Path.Combine(StateDirectory(), "node-state.json");
            if (File.Exists(path))
            {
                try { _state = JsonSerializer.Deserialize<NodeRuntimeState>(File.ReadAllText(path)) ?? new(); }
                catch { _state = new(State: "StateReadFailed"); }
            }
            _stateLoaded = true;
        }
    }
    private Task SaveStateAsync(CancellationToken token) =>
        AtomicWriteAsync(Path.Combine(StateDirectory(), "node-state.json"), JsonSerializer.Serialize(_state), token);

    private NodeAcknowledgement Ack(PrepareConfigurationRequest request, string stage) =>
        new(true, options.CurrentValue.NodeId, request.DeploymentId, stage);
    private NodeAcknowledgement Nack(PrepareConfigurationRequest request, string stage, string error) =>
        Nack(request.DeploymentId, stage, error);
    private NodeAcknowledgement Nack(string deploymentId, string stage, string error) =>
        new(false, options.CurrentValue.NodeId, deploymentId, stage, Error: error);

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record NodeRuntimeState(
        string? DeploymentId = null,
        string? SourceVersionId = null,
        string? ConfigVersionId = null,
        string? PreviousVersionId = null,
        string State = "Idle");
}
