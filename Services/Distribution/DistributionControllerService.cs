using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using Microsoft.Extensions.Options;
using NLog;
using System.Collections.Concurrent;
using LyWaf.Services.ManagementTasks;

namespace LyWaf.Services.Distribution;

/// <summary>控制节点编排灰度配置分发，持久化任务并汇总每个目标节点的 ACK/NACK。</summary>
public sealed class DistributionControllerService(
    IOptionsMonitor<DistributionOptions> options,
    ConfigVersionService versions,
    IDistributionNodeClient nodeClient,
    ManagementJobService? jobs = null,
    IHttpContextAccessor? contextAccessor = null)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ConfigurationDeployment> _deployments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scheduleGate = new();
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pauseRequested = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

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
        if (_running.ContainsKey(id) || jobs?.List().Any(t => t.Kind == "node-synchronization" && t.ResourceIds.Contains(id) &&
            t.State is ManagementJobState.Queued or ManagementJobState.Running) == true)
            throw new InvalidOperationException("同步任务仍在运行或排队，请等待结束后再继续");
        var deployment = Get(id);
        if (deployment.State is "Completed" or "RolledBack") throw new InvalidOperationException($"任务状态 {deployment.State} 不能继续");
        _pauseRequested.TryRemove(id, out _);
        deployment = deployment with { State = "Running" };
        await StoreAsync(deployment, cancellationToken);
        await ScheduleAsync(id);
        return deployment;
    }

    public async Task<ConfigurationDeployment> PauseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        _pauseRequested[id] = 0;
        var current = Get(id);
        var deployment = current with { State = _running.ContainsKey(id) ? "Pausing" : "Paused" };
        await StoreAsync(deployment, cancellationToken);
        return deployment;
    }

    public async Task<ConfigurationDeployment> RollbackAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var current = Get(id);
        if (_running.ContainsKey(id)) throw new InvalidOperationException("任务仍在运行，请先暂停并等待当前批次结束");
        var deployment = current with { State = "RollingBack" };
        await StoreAsync(deployment, cancellationToken);
        foreach (var nodeId in deployment.NodeOrder.Where(x => deployment.Targets[x].State == "Ack"))
        {
            NodeAcknowledgement ack;
            try { ack = await nodeClient.CommandAsync(nodeId, "rollback", new DistributionCommandRequest(id), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { ack = new(false, nodeId, id, "rollback", Error: ex.Message); }
            deployment = UpdateTarget(deployment, nodeId, ack.Ack ? "RolledBack" : "RollbackNack", ack.ConfigVersionId, ack.Error);
            await StoreAsync(deployment, cancellationToken);
        }
        deployment = deployment with
        {
            State = deployment.Targets.Values.Any(x => x.State == "RollbackNack") ? "RollbackFailed" : "RolledBack"
        };
        await StoreAsync(deployment, cancellationToken);
        return deployment;
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
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var deployment = Get(id);
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
                    deployment = UpdateTarget(deployment, nodeId, "Preparing");
                    await StoreAsync(deployment, cancellationToken);
                    NodeAcknowledgement ack;
                    try
                    {
                        ack = await nodeClient.PrepareAsync(nodeId,
                            new(deployment.Id, deployment.VersionId, deployment.Sha256, content), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        ack = new(false, nodeId, deployment.Id, "prepare", Error: ex.Message);
                    }
                    deployment = UpdateTarget(deployment, nodeId, ack.Ack ? "Prepared" : "Nack", ack.ConfigVersionId, ack.Error);
                    await StoreAsync(deployment, cancellationToken);
                    if (ack.Ack) prepared.Add(nodeId);
                }
                if (prepared.Count != batch.Length)
                {
                    deployment = deployment with { State = "Failed" };
                    await StoreAsync(deployment, cancellationToken);
                    return;
                }

                var committed = new List<string>();
                foreach (var nodeId in prepared)
                {
                    NodeAcknowledgement ack;
                    try { ack = await nodeClient.CommandAsync(nodeId, "commit", new(deployment.Id), cancellationToken); }
                    catch (Exception ex) { ack = new(false, nodeId, deployment.Id, "commit", Error: ex.Message); }
                    deployment = UpdateTarget(deployment, nodeId, ack.Ack ? "Ack" : "Nack", ack.ConfigVersionId, ack.Error);
                    await StoreAsync(deployment, cancellationToken);
                    if (ack.Ack) committed.Add(nodeId);
                    else
                    {
                        using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        foreach (var applied in committed)
                        {
                            NodeAcknowledgement rollback;
                            try { rollback = await nodeClient.CommandAsync(applied, "rollback", new(deployment.Id), rollbackTimeout.Token); }
                            catch (Exception ex) { rollback = new(false, applied, deployment.Id, "rollback", Error: ex.Message); }
                            deployment = UpdateTarget(deployment, applied, rollback.Ack ? "RolledBack" : "RollbackNack",
                                rollback.ConfigVersionId, rollback.Error);
                        }
                        deployment = deployment with { State = "Failed" };
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
        finally { _running.TryRemove(id, out _); }
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
                        if (deployment.State == "Failed") await StoreAsync(deployment with { State = "Running" }, token);
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
                await using var stream = File.OpenRead(path);
                var item = await JsonSerializer.DeserializeAsync<ConfigurationDeployment>(stream, JsonOptions, cancellationToken);
                if (item != null) _deployments[item.Id] = item.State == "Running" ? item with { State = "Paused" } : item;
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
            _deployments[deployment.Id] = deployment;
            var directory = Path.GetFullPath(options.CurrentValue.StateDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"deployment-{deployment.Id}.json");
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(deployment, JsonOptions), Encoding.UTF8, cancellationToken);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private static ConfigurationDeployment UpdateTarget(
        ConfigurationDeployment deployment, string nodeId, string state,
        string? configVersion = null, string? error = null)
    {
        var targets = deployment.Targets.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        targets[nodeId] = new(nodeId, state, DateTimeOffset.UtcNow, configVersion, error);
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
