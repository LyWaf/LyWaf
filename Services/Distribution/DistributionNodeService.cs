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
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private NodeRuntimeState _state = new();
    private bool _stateLoaded;
    internal Action<string>? RollbackFaultPoint { get; set; }

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
            ConfigurationStorageBudget.CheckContent(request.Content);
            if (string.IsNullOrWhiteSpace(request.VersionId) || request.VersionId.Length > 200)
                return Nack(request, "prepare", "Invalid configuration version identity", NodeConfigurationErrors.Invalid);
            NodeLocalConfigurationOverlay.ValidateCompatibility(SharedData.ConfigFilePath, request.Content);
            if (_state.State == "StateReadFailed") return Nack(request, "prepare", "节点状态损坏，拒绝配置写入", NodeConfigurationErrors.State);
            if (_state.RollbackStarted) throw new NodeConfigurationOutcomeUnknownException();
            if (File.Exists(PreparedPath(request.DeploymentId) + ".expired"))
                return Nack(request, "prepare", "候选已过期，不能复用任务 ID", NodeConfigurationErrors.Expired);
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Content))).ToLowerInvariant();
            if (!actual.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                return Nack(request, "prepare", "配置摘要不匹配", NodeConfigurationErrors.Digest);
            var existing = await ReadPreparedAsync(request.DeploymentId, cancellationToken);
            if (existing is not null && (existing.VersionId != request.VersionId ||
                !existing.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase) || existing.Content != request.Content))
                return Nack(request, "prepare", "任务 ID 已用于不同配置", NodeConfigurationErrors.Conflict);
            if (_state.DeploymentId == request.DeploymentId &&
                _state.SourceVersionId == request.VersionId &&
                _state.State is "Prepared" or "Applied")
            {
                if (_state.CommitStarted) throw new NodeConfigurationOutcomeUnknownException();
                if (_state.State == "Prepared" && CandidateExpired())
                    return Nack(request, "prepare", "候选已过期", NodeConfigurationErrors.Expired);
                return new(true, options.CurrentValue.NodeId, request.DeploymentId, "prepare", _state.ConfigVersionId);
            }
            if (existing is not null) return Nack(request, "prepare", "历史任务不能重新准备", NodeConfigurationErrors.Conflict);
            if (_state.State == "Prepared") return Nack(request, "prepare", "已有配置任务等待提交", NodeConfigurationErrors.Conflict);
            var (baselineHash, localContent) = await ReadCurrentConfigAsync(cancellationToken);
            var effective = NodeLocalConfigurationOverlay.Compose(SharedData.ConfigFilePath, localContent, request.Content);
            ConfigurationStorageBudget.CheckContent(effective);
            versions.ValidateMainCandidate(SharedData.ConfigFilePath, effective);
            using (var candidateRoot = NodeLocalConfigurationOverlay.ReadConfiguration(SharedData.ConfigFilePath, effective))
                lifecycle.Validate(candidateRoot);
            if (baselineHash != await CurrentConfigHashAsync(cancellationToken))
                return Nack(request, "prepare", "配置基准已变化，请重新创建任务", NodeConfigurationErrors.Conflict);
            var path = PreparedPath(request.DeploymentId);
            ConfigurationStorageBudget.Reserve(Path.GetDirectoryName(path)!,
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request)) + Encoding.UTF8.GetByteCount(effective),
                2, ConfigurationStorageBudget.PreparedBytes, 4096);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicWriteAsync(path, JsonSerializer.Serialize(request), cancellationToken);
            await AtomicWriteAsync(path + ".overlay", effective, cancellationToken);
            await PublishStateAsync(new(request.DeploymentId, request.VersionId, null, null, "Prepared", baselineHash,
                actual, ContentHash(effective), DateTimeOffset.UtcNow.AddMinutes(30)), cancellationToken);
            return Ack(request, "prepare");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (_state.CommitStarted || _state.RollbackStarted) { throw; }
        catch (Exception ex) when (IsValidationFailure(ex)) { return Failure(request.DeploymentId, "prepare", ex); }
        finally { _gate.Release(); }
    }

    public async Task<NodeAcknowledgement?> ReconcileCommandAsync(string operation, string deploymentId,
        PrepareConfigurationRequest? prepared, CancellationToken cancellationToken)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(StateDirectory(), "node-state.json");
            if (!File.Exists(path)) return null;
            NodeRuntimeState? persisted;
            try { persisted = JsonSerializer.Deserialize<NodeRuntimeState>(await File.ReadAllTextAsync(path, cancellationToken)); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
            if (persisted is null || persisted.DeploymentId != deploymentId) return null;
            if (operation == "configuration.rollback" && persisted.RollbackStarted)
            {
                var effectivePath = PreparedPath(deploymentId) + ".rollback";
                if (persisted.RollbackEffectiveHash is null || persisted.RollbackBaselineHash is null
                    || persisted.RollbackBaselineContentHash is null
                    || !File.Exists(effectivePath)) return null;
                var effective = await File.ReadAllTextAsync(effectivePath, cancellationToken);
                if (ContentHash(effective) != persisted.RollbackEffectiveHash) return null;
                var targetPath = Path.GetFullPath(SharedData.ConfigFilePath);
                var index = await versions.GetVersionsAsync(cancellationToken);
                var latest = index.FirstOrDefault(version =>
                    string.Equals(version.TargetPath, targetPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (latest is null || latest.Action != $"distribution-rollback:{deploymentId}"
                    || !latest.Sha256.Equals(persisted.RollbackEffectiveHash, StringComparison.OrdinalIgnoreCase)
                    || await versions.GetVersionContentAsync(latest.Id, cancellationToken) != effective
                    || await File.ReadAllTextAsync(targetPath, cancellationToken) != effective) return null;
                var baseline = index.FirstOrDefault(version => version.Id == latest.PreviousVersionId);
                if (baseline is null || !baseline.Sha256.Equals(persisted.RollbackBaselineContentHash, StringComparison.OrdinalIgnoreCase)
                    || await versions.GetVersionContentAsync(baseline.Id, cancellationToken) is not { } baselineContent
                    || ContentHash(baselineContent) != persisted.RollbackBaselineContentHash) return null;
                if (configuration is not IConfigurationRoot rollbackRoot) return null;
                lifecycle.ReloadAndValidate(rollbackRoot, lifecycle.Capture(rollbackRoot));
                await versions.RetainVersionsAsync(RetentionOwner(deploymentId), [latest.Id], cancellationToken);
                await PublishStateAsync(persisted with { State = "RolledBack", ConfigVersionId = latest.Id,
                    RollbackStarted = false }, cancellationToken);
                persisted = _state;
            }
            if (operation == "configuration.commit" && persisted.State == "Prepared")
            {
                var candidate = await ReadPreparedAsync(deploymentId, cancellationToken);
                if (candidate is null || candidate.DeploymentId != deploymentId || candidate.VersionId != persisted.SourceVersionId)
                    return null;
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Content)));
                if (!digest.Equals(persisted.CandidateHash, StringComparison.OrdinalIgnoreCase) ||
                    !digest.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
                var effective = await ReadEffectiveAsync(deploymentId, persisted.EffectiveHash, cancellationToken);
                if (effective is null) return null;
                var targetPath = Path.GetFullPath(SharedData.ConfigFilePath);
                var latest = (await versions.GetVersionsAsync(cancellationToken))
                    .FirstOrDefault(version => string.Equals(version.TargetPath, targetPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (latest is null || latest.Action != $"distribution:{deploymentId}" ||
                    !ContentHash(effective).Equals(latest.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    await versions.GetVersionContentAsync(latest.Id, cancellationToken) != effective ||
                    await File.ReadAllTextAsync(targetPath, cancellationToken) != effective) return null;
                if (configuration is not IConfigurationRoot root) return null;
                var before = lifecycle.Capture(root);
                lifecycle.ReloadAndValidate(root, before);
                await versions.RetainVersionsAsync(RetentionOwner(deploymentId), latest.PreviousVersionId is { } previous
                    ? [latest.Id, previous] : [latest.Id], cancellationToken);
                await PublishStateAsync(new(deploymentId, candidate.VersionId, latest.Id, latest.PreviousVersionId, "Applied"), cancellationToken);
                persisted = _state;
            }
            if (operation == "configuration.prepare" && persisted.State is "Prepared" or "Applied")
            {
                var stored = await ReadPreparedAsync(deploymentId, cancellationToken);
                if (prepared is null || stored != prepared) return null;
                return new(true, options.CurrentValue.NodeId, deploymentId, "prepare", persisted.ConfigVersionId);
            }
            if (operation == "configuration.commit" && persisted.State == "Applied")
                return new(true, options.CurrentValue.NodeId, deploymentId, "commit", persisted.ConfigVersionId);
            if (operation == "configuration.rollback" && persisted.State == "RolledBack")
                return new(true, options.CurrentValue.NodeId, deploymentId, "rollback", persisted.ConfigVersionId);
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task<NodeAcknowledgement> CommitAsync(string deploymentId, CancellationToken cancellationToken)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.State == "StateReadFailed") return Nack(deploymentId, "commit", "节点状态损坏，拒绝配置写入", NodeConfigurationErrors.State);
            if (_state.DeploymentId == deploymentId && _state.State == "Applied")
                return new(true, options.CurrentValue.NodeId, deploymentId, "commit", _state.ConfigVersionId);
            if (_state.DeploymentId != deploymentId || _state.State != "Prepared")
                return Nack(deploymentId, "commit", "任务不是当前待提交配置", NodeConfigurationErrors.State);
            if (_state.CommitStarted) throw new NodeConfigurationOutcomeUnknownException();
            if (CandidateExpired() || File.Exists(PreparedPath(deploymentId) + ".expired"))
                return Nack(deploymentId, "commit", "候选已过期或缺少有效期限", NodeConfigurationErrors.Expired);
            if (_state.BaselineHash is null || _state.BaselineHash != await CurrentConfigHashAsync(cancellationToken))
                return Nack(deploymentId, "commit", "配置基准已变化，请重新创建任务", NodeConfigurationErrors.Conflict);
            var prepared = await ReadPreparedAsync(deploymentId, cancellationToken);
            if (prepared == null) return Nack(deploymentId, "commit", "候选配置不存在", NodeConfigurationErrors.Missing);
            NodeLocalConfigurationOverlay.ValidateCompatibility(SharedData.ConfigFilePath, prepared.Content);
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prepared.Content))).ToLowerInvariant();
            if (prepared.DeploymentId != deploymentId || prepared.VersionId != _state.SourceVersionId ||
                !actual.Equals(prepared.Sha256, StringComparison.OrdinalIgnoreCase) || actual != _state.CandidateHash)
                return Nack(deploymentId, "commit", "候选配置摘要或身份不匹配", NodeConfigurationErrors.Digest);
            var effective = await ReadEffectiveAsync(deploymentId, _state.EffectiveHash, cancellationToken);
            if (effective is null) return Nack(deploymentId, "commit", "节点合成候选缺失或摘要不匹配，请重新创建任务", NodeConfigurationErrors.Missing);
            var currentContent = await File.ReadAllTextAsync(SharedData.ConfigFilePath, cancellationToken);
            if (NodeLocalConfigurationOverlay.Compose(SharedData.ConfigFilePath, currentContent, prepared.Content) != effective)
                return Nack(deploymentId, "commit", "节点本地配置或解析环境已变化，请重新创建任务", NodeConfigurationErrors.Conflict);
            var root = configuration as IConfigurationRoot ?? throw new InvalidOperationException("配置根不支持重载");
            var before = lifecycle.Capture(root);
            if (CandidateExpired()) return Nack(deploymentId, "commit", "候选已过期", NodeConfigurationErrors.Expired);
            // Persist uncertainty before the first possible effect, so cleanup cannot erase reconciliation evidence.
            await PublishStateAsync(_state with { CommitStarted = true }, cancellationToken);
            if (CandidateExpired()) throw new NodeConfigurationOutcomeUnknownException();
            var written = await versions.WriteMainIfUnchangedAsync(SharedData.ConfigFilePath, effective,
                $"distribution:{deploymentId}", _state.BaselineHash, cancellationToken, RetentionOwner(deploymentId));
            try { lifecycle.ReloadAndValidate(root, before); }
            catch
            {
                await versions.WriteMainIfUnchangedAsync(SharedData.ConfigFilePath, currentContent,
                    $"distribution-restore:{deploymentId}", written.Sha256, CancellationToken.None);
                root.Reload();
                throw;
            }
            await PublishStateAsync(new(deploymentId, prepared.VersionId, written.Id, written.PreviousVersionId, "Applied"), cancellationToken);
            return new(true, options.CurrentValue.NodeId, deploymentId, "commit", written.Id);
        }
        // Once an effect may have started, leave the Inbox entry Unknown rather than persist a false NACK.
        catch (Exception) when (_state.CommitStarted) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsValidationFailure(ex)) { return Failure(deploymentId, "commit", ex); }
        finally { _gate.Release(); }
    }

    public async Task<NodeAcknowledgement> RollbackAsync(string deploymentId, CancellationToken cancellationToken)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.RollbackStarted)
                throw new NodeConfigurationOutcomeUnknownException();
            if (_state.State == "StateReadFailed") return Nack(deploymentId, "rollback", "节点状态损坏，拒绝配置写入", NodeConfigurationErrors.State);
            if (_state.DeploymentId == deploymentId && _state.State == "Prepared")
            {
                if (_state.CommitStarted) throw new NodeConfigurationOutcomeUnknownException();
                // Abandon a candidate without touching the active configuration.
                await PublishStateAsync(_state with { State = "RolledBack" }, cancellationToken);
                return new(true, options.CurrentValue.NodeId, deploymentId, "rollback", _state.ConfigVersionId);
            }
            if (_state.DeploymentId == deploymentId && _state.State == "RolledBack")
                return new(true, options.CurrentValue.NodeId, deploymentId, "rollback", _state.ConfigVersionId);
            if (_state.DeploymentId != deploymentId || _state.State != "Applied"
                || string.IsNullOrWhiteSpace(_state.PreviousVersionId) || string.IsNullOrWhiteSpace(_state.ConfigVersionId))
                return Nack(deploymentId, "rollback", "没有该任务的可回滚版本", NodeConfigurationErrors.State);
            await versions.RetainVersionsAsync(RetentionOwner(deploymentId),
                [_state.ConfigVersionId, _state.PreviousVersionId], cancellationToken);
            var (baseline, currentContent) = await ReadCurrentConfigAsync(cancellationToken);
            var appliedContent = await versions.GetVersionContentAsync(_state.ConfigVersionId, cancellationToken)
                ?? throw new InvalidOperationException("Applied version is missing");
            var expectedCurrent = NodeLocalConfigurationOverlay.Compose(SharedData.ConfigFilePath, currentContent, appliedContent);
            if (!ConfigurationEquals(currentContent, expectedCurrent))
                return Nack(deploymentId, "rollback", "本地业务配置已变化，拒绝覆盖，请人工核对", NodeConfigurationErrors.Conflict);
            var previousContent = await versions.GetVersionContentAsync(_state.PreviousVersionId, cancellationToken)
                ?? throw new InvalidOperationException("回滚版本不存在");
            var restoredContent = NodeLocalConfigurationOverlay.Compose(SharedData.ConfigFilePath, currentContent, previousContent);
            ConfigurationStorageBudget.CheckContent(restoredContent);
            using (var candidateRoot = NodeLocalConfigurationOverlay.ReadConfiguration(SharedData.ConfigFilePath, restoredContent))
                lifecycle.Validate(candidateRoot);
            var root = configuration as IConfigurationRoot ?? throw new InvalidOperationException("配置根不支持重载");
            var before = lifecycle.Capture(root);
            await AtomicWriteAsync(PreparedPath(deploymentId) + ".rollback", restoredContent, cancellationToken);
            await PublishStateAsync(_state with { State = "RollingBack", RollbackStarted = true,
                RollbackBaselineHash = baseline, RollbackBaselineContentHash = ContentHash(currentContent),
                RollbackEffectiveHash = ContentHash(restoredContent) }, cancellationToken);
            RollbackFaultPoint?.Invoke("IntentPersisted");
            var restored = await versions.WriteMainIfUnchangedAsync(SharedData.ConfigFilePath, restoredContent,
                $"distribution-rollback:{deploymentId}", baseline, cancellationToken, RetentionOwner(deploymentId));
            RollbackFaultPoint?.Invoke("ConfigurationWritten");
            lifecycle.ReloadAndValidate(root, before);
            RollbackFaultPoint?.Invoke("ConfigurationReloaded");
            await PublishStateAsync(_state with { ConfigVersionId = restored.Id, State = "RolledBack",
                RollbackStarted = false }, cancellationToken);
            RollbackFaultPoint?.Invoke("StatePersisted");
            return new(true, options.CurrentValue.NodeId, deploymentId, "rollback", restored.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (_state.CommitStarted || _state.RollbackStarted || _state.DeploymentId == deploymentId && _state.State == "RolledBack") { throw; }
        catch (Exception ex) when (IsValidationFailure(ex)) { return Failure(deploymentId, "rollback", ex); }
        finally { _gate.Release(); }
    }

    private async Task<PrepareConfigurationRequest?> ReadPreparedAsync(string deploymentId, CancellationToken token)
    {
        var path = PreparedPath(deploymentId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Prepared record exceeds capacity");
        return await JsonSerializer.DeserializeAsync<PrepareConfigurationRequest>(stream, cancellationToken: token);
    }

    private bool CandidateExpired() => _state.CandidateExpiresAt is not { } expires || expires <= DateTimeOffset.UtcNow;

    public async Task<int> CleanupExpiredCandidatesAsync(CancellationToken token)
    {
        EnsureStateLoaded();
        await _gate.WaitAsync(token);
        try
        {
            if (_state.State == "StateReadFailed") return 0;
            if (_state.State == "Prepared" && !_state.CommitStarted && _state.CandidateExpiresAt is { } expires
                && expires <= DateTimeOffset.UtcNow && _state.DeploymentId is { } currentId)
            {
                var marker = PreparedPath(currentId) + ".expired";
                // A durable, permanent fence precedes both state publication and payload deletion.
                await AtomicWriteAsync(marker, JsonSerializer.Serialize(new ExpiredCandidate(currentId, expires)), token);
                await PublishStateAsync(_state with { State = "Expired" }, token);
            }
            var directory = Path.Combine(StateDirectory(), "prepared");
            if (!Directory.Exists(directory)) return 0;
            var removed = 0;
            foreach (var marker in Directory.EnumerateFiles(directory, "*.json.expired"))
            {
                token.ThrowIfCancellationRequested();
                var expiredId = Path.GetFileName(marker)[..^".json.expired".Length];
                if (_state.DeploymentId == expiredId && _state.State != "Expired") continue;
                var fence = JsonSerializer.Deserialize<ExpiredCandidate>(await File.ReadAllTextAsync(marker, token));
                if (fence is null || fence.DeploymentId != expiredId || fence.ExpiresAt > DateTimeOffset.UtcNow)
                    throw new InvalidDataException("Invalid candidate expiration fence");
                var path = PreparedPath(expiredId);
                if (!File.Exists(path) && !File.Exists(path + ".overlay")) continue;
                File.Delete(path + ".overlay");
                File.Delete(path);
                if (++removed >= 100) break;
            }
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> ReadEffectiveAsync(string deploymentId, string? hash, CancellationToken token)
    {
        var path = PreparedPath(deploymentId) + ".overlay";
        if (hash is null || !File.Exists(path)) return null;
        var content = await File.ReadAllTextAsync(path, token);
        return ContentHash(content) == hash ? content : null;
    }

    private static string ContentHash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string RetentionOwner(string deploymentId) => "distribution:" + deploymentId;

    private bool ConfigurationEquals(string first, string second)
    {
        using var left = NodeLocalConfigurationOverlay.ReadConfiguration(SharedData.ConfigFilePath, first);
        using var right = NodeLocalConfigurationOverlay.ReadConfiguration(SharedData.ConfigFilePath, second);
        var a = lifecycle.Capture(left);
        var b = lifecycle.Capture(right);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private string PreparedPath(string deploymentId)
    {
        if (string.IsNullOrWhiteSpace(deploymentId) || deploymentId.Length > 128 ||
            deploymentId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("DeploymentId 无效");
        return Path.Combine(StateDirectory(), "prepared", deploymentId + ".json");
    }

    private string StateDirectory() => Path.GetFullPath(options.CurrentValue.StateDirectory);

    private static async Task<string> CurrentConfigHashAsync(CancellationToken token)
    {
        await using var stream = File.OpenRead(SharedData.ConfigFilePath);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static async Task<(string Hash, string Content)> ReadCurrentConfigAsync(CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(SharedData.ConfigFilePath, token);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (Convert.ToHexString(SHA256.HashData(bytes)), await reader.ReadToEndAsync(token));
    }

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
    private async Task PublishStateAsync(NodeRuntimeState state, CancellationToken token)
    {
        await AtomicWriteAsync(Path.Combine(StateDirectory(), "node-state.json"), JsonSerializer.Serialize(state), token);
        _state = state;
    }

    private NodeAcknowledgement Ack(PrepareConfigurationRequest request, string stage) =>
        new(true, options.CurrentValue.NodeId, request.DeploymentId, stage);
    private NodeAcknowledgement Nack(PrepareConfigurationRequest request, string stage, string error, string code) =>
        Nack(request.DeploymentId, stage, error, code);
    private NodeAcknowledgement Nack(string deploymentId, string stage, string error, string code) =>
        new(false, options.CurrentValue.NodeId, deploymentId, stage, Error: error,
            ErrorCode: code, MessageKey: "node.errors." + code);

    private static bool IsValidationFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or FormatException or JsonException or YamlDotNet.Core.YamlException;

    private NodeAcknowledgement Failure(string id, string stage, Exception ex)
    {
        var code = ex is IOException or UnauthorizedAccessException ? NodeConfigurationErrors.Storage : NodeConfigurationErrors.Invalid;
        return Nack(id, stage, code == NodeConfigurationErrors.Storage
            ? "Configuration storage is unavailable" : "Configuration validation failed", code);
    }

    private async Task AtomicWriteAsync(string path, string content, CancellationToken token)
    {
        if (Path.GetFileName(Path.GetDirectoryName(path)) == "prepared")
            ConfigurationStorageBudget.Reserve(Path.GetDirectoryName(path)!, Encoding.UTF8.GetByteCount(content), 1,
                ConfigurationStorageBudget.PreparedBytes, 4096);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), token);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ExpiredCandidate(string DeploymentId, DateTimeOffset ExpiresAt);

    private sealed record NodeRuntimeState(
        string? DeploymentId = null,
        string? SourceVersionId = null,
        string? ConfigVersionId = null,
        string? PreviousVersionId = null,
        string State = "Idle",
        string? BaselineHash = null,
        string? CandidateHash = null,
        string? EffectiveHash = null,
        DateTimeOffset? CandidateExpiresAt = null,
        bool CommitStarted = false,
        bool RollbackStarted = false,
        string? RollbackBaselineHash = null,
        string? RollbackEffectiveHash = null,
        string? RollbackBaselineContentHash = null);
}
