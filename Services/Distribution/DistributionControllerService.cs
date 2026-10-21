using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using Microsoft.Extensions.Options;
using NLog;
using System.Collections.Concurrent;
using LyWaf.Services.ManagementTasks;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.Distribution;

/// <summary>控制节点编排灰度配置分发，持久化任务并汇总每个目标节点的 ACK/NACK。</summary>
public sealed class DistributionControllerService(
    IOptionsMonitor<DistributionOptions> options,
    ConfigVersionService versions,
    IDistributionNodeClient nodeClient,
    ManagementJobService? jobs = null,
    IHttpContextAccessor? contextAccessor = null,
    IOptionsMonitor<NodeCommunicationOptions>? communication = null)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ConfigurationDeployment> _deployments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scheduleGate = new();
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pauseRequested = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deploymentGates = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim DeploymentGate(string id) => _deploymentGates.GetOrAdd(id, _ => new(1, 1));

    public async Task<IReadOnlyList<ConfigurationDeployment>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var tasks = jobs?.List() ?? [];
        return _deployments.Values.OrderByDescending(x => x.CreatedAt).Select(deployment =>
        {
            var task = tasks.FirstOrDefault(t => t.Kind == "node-synchronization" && t.ResourceIds.Contains(deployment.Id));
            if (task == null) return deployment;
            return deployment with
            {
                TaskId = task.Id,
                State = deployment.State is "Paused" or "RolledBack" ? deployment.State : task.State switch
                {
                    ManagementJobState.Queued => "Queued",
                    ManagementJobState.Cancelled => "Paused",
                    ManagementJobState.Failed or ManagementJobState.TimedOut => "Failed",
                    _ => deployment.State
                }
            };
        }).ToArray();
    }

    public async Task<ConfigurationDeployment> CreateAsync(CreateDistributionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.VersionId) || request.VersionId.Length > 200 ||
            request.NodeIds?.Count > 100 || request.NodeIds?.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 200) == true)
            throw new ArgumentException("版本或节点列表无效，最多选择 100 个节点");
        EnsureController();
        await EnsureLoadedAsync(cancellationToken);
        var content = await versions.GetVersionContentAsync(request.VersionId, cancellationToken)
            ?? throw new KeyNotFoundException("配置版本不存在");
        var configured = options.CurrentValue.Nodes.Where(x => x.Value.Enabled).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = (request.NodeIds?.Count > 0 ? request.NodeIds : configured.ToArray())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (order.Length == 0) throw new InvalidOperationException("没有可分发节点");
        var unknown = order.FirstOrDefault(x => !configured.Contains(x));
        if (unknown != null) throw new InvalidOperationException($"节点不存在或未启用: {unknown}");
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var targets = order.ToDictionary(x => x,
            x => new DistributionTargetState(x, "Pending", DateTimeOffset.UtcNow), StringComparer.OrdinalIgnoreCase);
        var deployment = new ConfigurationDeployment(id, request.VersionId, Hash(content), DateTimeOffset.UtcNow,
            "Running", Math.Clamp(request.CanaryCount, 1, order.Length), Math.Max(1, request.BatchSize),
            request.PauseAfterEachBatch, 0, order, targets);
        await StoreAsync(deployment, cancellationToken);
        await ScheduleAsync(id);
        return deployment;
    }

    public async Task<ConfigurationDeployment> ResumeAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (_running.ContainsKey(id)) throw new InvalidOperationException("Deployment is busy");
        var gate = DeploymentGate(id);
        await gate.WaitAsync(cancellationToken);
        try
        {
        if (_running.ContainsKey(id) || jobs?.List().Any(t => t.Kind == "node-synchronization" && t.ResourceIds.Contains(id) &&
            t.State is ManagementJobState.Queued or ManagementJobState.Running) == true)
            throw new InvalidOperationException("同步任务仍在运行或排队，请等待结束后再继续");
        var deployment = Get(id);
        if (deployment.State != "Paused")
            throw new InvalidOperationException($"任务状态 {deployment.State} 不能继续，请完成回滚并创建新任务");
        _pauseRequested.TryRemove(id, out _);
        deployment = deployment with { State = "Running" };
        await StoreAsync(deployment, cancellationToken);
        await ScheduleAsync(id);
        return deployment;
        }
        finally { gate.Release(); }
    }

    public async Task<ConfigurationDeployment> PauseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var snapshot = Get(id);
        if (snapshot.State is not ("Running" or "Pausing" or "Paused"))
            throw new InvalidOperationException($"任务状态 {snapshot.State} 不能暂停");
        // Signal the active processor before waiting for its snapshot ownership.
        _pauseRequested[id] = 0;
        var gate = DeploymentGate(id);
        if (_running.ContainsKey(id)) return snapshot with { State = "Pausing" };
        await gate.WaitAsync(cancellationToken);
        try
        {
        var current = Get(id);
        if (current.State is not ("Running" or "Pausing" or "Paused"))
            throw new InvalidOperationException($"任务状态 {current.State} 不能暂停");
        _pauseRequested[id] = 0;
        var deployment = current with { State = _running.ContainsKey(id) ? "Pausing" : "Paused" };
        await StoreAsync(deployment, cancellationToken);
        return deployment;
        }
        finally { gate.Release(); }
    }

    public async Task<ConfigurationDeployment> RollbackAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!_running.TryAdd(id, 0)) throw new InvalidOperationException("任务仍在运行，请先暂停并等待当前批次结束");
        var gate = DeploymentGate(id);
        var acquired = false;
        try
        {
        await gate.WaitAsync(cancellationToken);
        acquired = true;
        var current = Get(id);
        var deployment = current with { State = "RollingBack" };
        await StoreAsync(deployment, cancellationToken);
        foreach (var nodeId in deployment.NodeOrder.Where(x => deployment.Targets[x].State is
            "Ack" or "Prepared" or "Preparing" or "Unknown" or "RollbackNack"))
        {
            deployment = UpdateTarget(deployment, nodeId, "Unknown", pendingStage: "rollback");
            await StoreAsync(deployment, cancellationToken);
            NodeAcknowledgement ack;
            var unknown = false;
            try { ack = await nodeClient.CommandAsync(nodeId, "rollback", new DistributionCommandRequest(id), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { unknown = true; ack = new(false, nodeId, id, "rollback", Error: ex.Message); }
            deployment = UpdateTarget(deployment, nodeId, unknown ? "Unknown" : ack.Ack ? "RolledBack" : "RollbackNack", ack.ConfigVersionId, ack.Error,
                pendingStage: unknown ? "rollback" : null);
            await StoreAsync(deployment, cancellationToken);
        }
        deployment = deployment with
        {
            State = deployment.Targets.Values.Any(x => x.State is "RollbackNack" or "Unknown") ? "RollbackFailed" : "RolledBack"
        };
        await StoreAsync(deployment, cancellationToken);
        return deployment;
        }
        finally { if (acquired) gate.Release(); _running.TryRemove(id, out _); }
    }

    public async Task<IReadOnlyDictionary<string, DistributionNodeStatus?>> GetNodeStatusesAsync(CancellationToken cancellationToken)
    {
        EnsureController();
        var result = new Dictionary<string, DistributionNodeStatus?>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeId in options.CurrentValue.Nodes.Where(x => x.Value.Enabled).Select(x => x.Key))
        {
            try { result[nodeId] = await nodeClient.GetStatusAsync(nodeId, cancellationToken); }
            catch { result[nodeId] = null; }
        }
        return result;
    }

    private async Task ProcessAsync(string id, CancellationToken cancellationToken)
    {
        var gate = DeploymentGate(id);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var deployment = Get(id);
            if (jobs != null && deployment.State == "Failed")
            {
                deployment = deployment with { State = "Running" };
                await StoreAsync(deployment, cancellationToken);
            }
            if (deployment.State != "Running") return;
            var content = await versions.GetVersionContentAsync(deployment.VersionId, cancellationToken)
                ?? throw new InvalidOperationException("源配置版本已不存在");
            while (deployment.NextIndex < deployment.NodeOrder.Count && deployment.State == "Running")
            {
                if (_pauseRequested.ContainsKey(id))
                {
                    deployment = deployment with { State = "Paused" };
                    await StoreAsync(deployment, cancellationToken);
                    return;
                }
                var count = deployment.NextIndex == 0 ? deployment.CanaryCount : deployment.BatchSize;
                var batch = deployment.NodeOrder.Skip(deployment.NextIndex).Take(count).ToArray();
                var prepared = new List<string>();
                foreach (var nodeId in batch)
                {
                    if (deployment.Targets[nodeId].State == "Ack")
                    {
                        prepared.Add(nodeId);
                        continue;
                    }
                    deployment = UpdateTarget(deployment, nodeId, "Unknown", pendingStage: "prepare");
                    await StoreAsync(deployment, cancellationToken);
                    NodeAcknowledgement ack;
                    try
                    {
                        ack = await nodeClient.PrepareAsync(nodeId,
                            new(deployment.Id, deployment.VersionId, deployment.Sha256, content), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // Legacy status lacks a content digest: only a repeated prepare can confirm it.
                        deployment = UpdateTarget(deployment, nodeId, "Unknown", error: ex.Message, pendingStage: "prepare");
                        await StoreAsync(deployment with { State = "Paused" }, CancellationToken.None);
                        return;
                    }
                    deployment = UpdateTarget(deployment, nodeId, ack.Ack ? "Prepared" : "Nack", ack.ConfigVersionId, ack.Error);
                    await StoreAsync(deployment, cancellationToken);
                    if (ack.Ack) prepared.Add(nodeId);
                }
                if (prepared.Count != batch.Length)
                {
                    using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    foreach (var nodeId in prepared)
                    {
                        deployment = UpdateTarget(deployment, nodeId, "Unknown", pendingStage: "rollback");
                        await StoreAsync(deployment, CancellationToken.None);
                        NodeAcknowledgement aborted;
                        var unknown = false;
                        try { aborted = await nodeClient.CommandAsync(nodeId, "rollback", new(deployment.Id), abortTimeout.Token); }
                        catch (Exception ex) { unknown = true; aborted = new(false, nodeId, deployment.Id, "rollback", Error: ex.Message); }
                        deployment = UpdateTarget(deployment, nodeId, unknown ? "Unknown" : aborted.Ack ? "RolledBack" : "RollbackNack",
                            aborted.ConfigVersionId, aborted.Error, pendingStage: unknown ? "rollback" : null);
                    }
                    deployment = deployment with { State = prepared.Any(nodeId => deployment.Targets[nodeId].State is "RollbackNack" or "Unknown") ? "RollbackFailed" : "Failed" };
                    await StoreAsync(deployment, cancellationToken);
                    return;
                }

                foreach (var nodeId in prepared)
                {
                    if (deployment.Targets[nodeId].State == "Ack") continue;
                    deployment = UpdateTarget(deployment, nodeId, "Unknown", pendingStage: "commit");
                    await StoreAsync(deployment, cancellationToken);
                    NodeAcknowledgement ack;
                    try { ack = await nodeClient.CommandAsync(nodeId, "commit", new(deployment.Id), cancellationToken); }
                    catch (Exception ex)
                    {
                        // A lost response does not prove that the node failed to apply the configuration.
                        DistributionNodeStatus? status = null;
                        try { status = await nodeClient.GetStatusAsync(nodeId, cancellationToken); }
                        catch { }
                        if (status?.NodeId == nodeId && status.LastDeploymentId == deployment.Id && status.State == "Applied")
                            ack = new(true, nodeId, deployment.Id, "commit", status.ConfigVersionId);
                        else
                        {
                            deployment = UpdateTarget(deployment, nodeId, "Unknown", error: ex.Message, pendingStage: "commit");
                            await StoreAsync(deployment with { State = "Paused" }, CancellationToken.None);
                            return;
                        }
                    }
                    deployment = UpdateTarget(deployment, nodeId, ack.Ack ? "Ack" : "Nack", ack.ConfigVersionId, ack.Error);
                    await StoreAsync(deployment, cancellationToken);
                    if (!ack.Ack)
                    {
                        using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        foreach (var applied in prepared)
                        {
                            deployment = UpdateTarget(deployment, applied, "Unknown", pendingStage: "rollback");
                            await StoreAsync(deployment, CancellationToken.None);
                            NodeAcknowledgement rollback;
                            var unknown = false;
                            try { rollback = await nodeClient.CommandAsync(applied, "rollback", new(deployment.Id), rollbackTimeout.Token); }
                            catch (Exception ex) { unknown = true; rollback = new(false, applied, deployment.Id, "rollback", Error: ex.Message); }
                            deployment = UpdateTarget(deployment, applied, unknown ? "Unknown" : rollback.Ack ? "RolledBack" : "RollbackNack",
                                rollback.ConfigVersionId, rollback.Error, pendingStage: unknown ? "rollback" : null);
                        }
                        deployment = deployment with { State = prepared.Any(nodeId => deployment.Targets[nodeId].State is "RollbackNack" or "Unknown") ? "RollbackFailed" : "Failed" };
                        await StoreAsync(deployment, CancellationToken.None);
                        return;
                    }
                }

                deployment = deployment with { NextIndex = deployment.NextIndex + batch.Length };
                if (deployment.NextIndex >= deployment.NodeOrder.Count)
                    deployment = deployment with { State = "Completed" };
                else if (deployment.PauseAfterEachBatch || _pauseRequested.ContainsKey(id))
                    deployment = deployment with { State = "Paused" };
                await StoreAsync(deployment, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "配置分发任务失败: {DeploymentId}", id);
            if (_deployments.TryGetValue(id, out var deployment))
                await StoreAsync(deployment with { State = "Failed" }, CancellationToken.None);
        }
        finally { _running.TryRemove(id, out _); gate.Release(); }
    }

    /// <summary>Backfills durable outcomes only; never schedules work or advances a batch.</summary>
    public async Task<int> RecoverCompletedOutboxAsync(CancellationToken cancellationToken)
    {
        var config = options.CurrentValue;
        if (!config.Enabled || !config.Role.Equals("Controller", StringComparison.OrdinalIgnoreCase)) return 0;
        var transport = communication?.CurrentValue;
        if (transport is not { Enabled: true, Role: "Controller" }) return 0;
        await EnsureLoadedAsync(cancellationToken);
        var records = nodeClient.ReadOutbox();
        var recovered = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = record.Message;
            var ack = record.Result;
            var confirmed = record.State == "Completed" && ack is { Ack: true };
            var rejected = record.State == "Rejected" && ack is { Ack: false }
                && NodeConfigurationErrors.IsKnown(ack.ErrorCode) && ack.ErrorCode != NodeConfigurationErrors.Unknown;
            if ((!confirmed && !rejected) || record.Attempts <= 0 || ack is null
                || message.ClusterId != transport.ClusterId || message.SenderNodeId != transport.NodeId
                || message.Type != "configuration." + ack.Stage
                || ack.Stage is not ("prepare" or "commit" or "rollback")
                || message.ReceiverNodeId != ack.NodeId || message.CorrelationId != ack.DeploymentId
                || message.Payload.ValueKind != JsonValueKind.Object
                || !message.Payload.TryGetProperty("deploymentId", out var payloadId)
                || payloadId.ValueKind != JsonValueKind.String || payloadId.GetString() != ack.DeploymentId
                || !_deployments.ContainsKey(ack.DeploymentId)) continue;
            var gate = DeploymentGate(ack.DeploymentId);
            if (!await gate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                if (_running.ContainsKey(ack.DeploymentId)
                    || jobs?.List().Any(t => t.Kind == "node-synchronization" && t.ResourceIds.Contains(ack.DeploymentId)
                        && t.State is ManagementJobState.Queued or ManagementJobState.Running) == true) continue;
                var deployment = Get(ack.DeploymentId);
                if (deployment.Id != ack.DeploymentId
                    || !deployment.Targets.TryGetValue(ack.NodeId, out var target)
                    || target.NodeId != ack.NodeId || target.State != "Unknown"
                    || target.PendingStage != ack.Stage) continue;
                var state = rejected ? (ack.Stage == "rollback" ? "RollbackNack" : "Nack")
                    : ack.Stage switch { "prepare" => "Prepared", "commit" => "Ack", _ => "RolledBack" };
                await StoreAsync(UpdateTarget(deployment, ack.NodeId, state, ack.ConfigVersionId,
                    rejected ? ack.ErrorCode : null), cancellationToken);
                recovered++;
            }
            finally { gate.Release(); }
        }
        return recovered;
    }

    private async Task ScheduleAsync(string id)
    {
        if (jobs == null)
        {
            if (_running.TryAdd(id, 0)) _ = Task.Run(() => ProcessAsync(id, CancellationToken.None));
            return;
        }
        try
        {
            lock (_scheduleGate)
            {
                if (jobs.List().Any(t => t.Kind == "node-synchronization" && t.ResourceIds.Contains(id) &&
                    t.State is ManagementJobState.Queued or ManagementJobState.Running)) return;
                var actor = contextAccessor?.HttpContext?.Items["Username"]?.ToString() ?? "system";
                jobs.Enqueue("node-synchronization", actor, async (log, token) =>
                {
                    if (!_running.TryAdd(id, 0)) throw new InvalidDataException("节点同步仍在运行");
                    try
                    {
                        var deployment = Get(id);
                        log($"开始节点同步: {id}");
                        await ProcessAsync(id, token);
                        token.ThrowIfCancellationRequested();
                        deployment = Get(id);
                        foreach (var target in deployment.Targets.Values)
                            log($"{target.NodeId}: {target.State}{(target.Error == null ? "" : " - " + target.Error)}");
                        if (deployment.State == "Failed") throw new InvalidDataException("节点同步失败，请查看逐节点结果");
                        log($"本次批次完成，分发状态: {deployment.State}");
                    }
                    finally { _running.TryRemove(id, out _); }
                }, TimeSpan.FromMinutes(30), [id]);
            }
        }
        catch
        {
            await StoreAsync(Get(id) with { State = "Failed" }, CancellationToken.None);
            throw;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;
            var directory = Path.GetFullPath(options.CurrentValue.StateDirectory);
            if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "deployment-*.json"))
            {
                // ReadAllText accepts the BOM emitted by older controller versions.
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                var item = JsonSerializer.Deserialize<ConfigurationDeployment>(content, JsonOptions);
                if (item != null) _deployments[item.Id] = item.State switch
                {
                    "Running" or "Pausing" => item with { State = "Paused" },
                    "RollingBack" => item with { State = "RollbackFailed" },
                    _ => item
                };
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    private ConfigurationDeployment Get(string id) =>
        _deployments.TryGetValue(id, out var deployment) ? deployment : throw new KeyNotFoundException("分发任务不存在");

    private async Task StoreAsync(ConfigurationDeployment deployment, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetFullPath(options.CurrentValue.StateDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"deployment-{deployment.Id}.json");
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, deployment, JsonOptions, cancellationToken);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
                _deployments[deployment.Id] = deployment;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private static ConfigurationDeployment UpdateTarget(
        ConfigurationDeployment deployment, string nodeId, string state,
        string? configVersion = null, string? error = null, string? pendingStage = null)
    {
        var targets = deployment.Targets.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        targets[nodeId] = new(nodeId, state, DateTimeOffset.UtcNow, configVersion, error, pendingStage);
        return deployment with { Targets = targets };
    }

    private void EnsureController()
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !current.Role.Equals("Controller", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前实例不是配置分发控制节点");
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
