using System.Collections.Concurrent;
using System.Globalization;
using LyWaf.Services.Metrics;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Policy;

/// <summary>
/// 表示一个上游目标的异常检测运行快照，供控制台和诊断接口展示驱逐原因、恢复时间及近期失败统计。
/// 该模型只读，不参与 YARP 的目标选择或健康状态更新。
/// </summary>
/// <param name="ClusterId">目标所属的 YARP Cluster ID。</param>
/// <param name="DestinationId">Cluster 内用于唯一标识上游目标的 Destination ID。</param>
/// <param name="Address">上游目标的实际访问地址。</param>
/// <param name="State">当前可用状态；值为 <c>available</c> 或 <c>ejected</c>。</param>
/// <param name="Reason">最近一次驱逐原因；目标未被驱逐时通常为空。</param>
/// <param name="EjectedUntil">本次驱逐的预计恢复时间；目标可用时为空。</param>
/// <param name="ConsecutiveFailures">当前连续连接失败次数。</param>
/// <param name="Consecutive5xx">当前连续 HTTP 5xx 响应次数。</param>
/// <param name="WindowRequests">固定大小滑动窗口内已统计的请求数。</param>
/// <param name="WindowFailures">固定大小滑动窗口内的失败请求数。</param>
/// <param name="ErrorRate">滑动窗口失败比例，取值范围为 0 到 1。</param>
public sealed record OutlierSnapshot(
    string ClusterId, string DestinationId, string Address, string State, string? Reason,
    DateTimeOffset? EjectedUntil, int ConsecutiveFailures, int Consecutive5xx,
    int WindowRequests, int WindowFailures, double ErrorRate);

/// <summary>
/// 保存各 Cluster 目标的被动异常统计和驱逐状态。
/// 注册表是进程级单例，配置热更新后的新目标会按 ClusterId 和 DestinationId 惰性创建记录。
/// </summary>
public sealed class OutlierDetectionRegistry(PrometheusMetrics metrics)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal Entry GetOrAdd(string clusterId, DestinationState destination, OutlierPolicy policy)
        => _entries.GetOrAdd(clusterId + "\n" + destination.DestinationId,
            _ => new Entry(clusterId, destination.DestinationId, destination.Model.Config.Address, policy, metrics));

    /// <summary>
    /// 获取所有已被观察目标的一致性只读快照，按 Cluster 和 Destination 排序。
    /// 读取时会同步清理由驱逐超时而自动恢复的状态。
    /// </summary>
    public IReadOnlyList<OutlierSnapshot> GetSnapshots()
        => _entries.Values.Select(static entry => entry.Snapshot()).OrderBy(static x => x.ClusterId)
            .ThenBy(static x => x.DestinationId).ToArray();

    /// <summary>
    /// 保存单个目标的固定大小滑动窗口和连续失败计数；所有复合状态在同一锁内更新。
    /// </summary>
    internal sealed class Entry(
        string clusterId, string destinationId, string address, OutlierPolicy policy, PrometheusMetrics metrics)
    {
        private readonly object _gate = new();
        private readonly bool[] _window = new bool[policy.WindowSize];
        private int _windowIndex;
        private int _windowCount;
        private int _windowFailures;
        private int _consecutiveFailures;
        private int _consecutive5xx;
        private string? _reason;
        private DateTimeOffset? _ejectedUntil;

        public string Record(bool connectionFailure, bool serverError)
        {
            lock (_gate)
            {
                var failed = connectionFailure || serverError;
                if (_windowCount == _window.Length)
                {
                    if (_window[_windowIndex]) _windowFailures--;
                }
                else _windowCount++;
                _window[_windowIndex] = failed;
                if (failed) _windowFailures++;
                _windowIndex = (_windowIndex + 1) % _window.Length;

                _consecutiveFailures = connectionFailure ? _consecutiveFailures + 1 : 0;
                _consecutive5xx = serverError ? _consecutive5xx + 1 : 0;
                if (_consecutiveFailures >= policy.ConsecutiveFailures) return "consecutive_connection_failures";
                if (_consecutive5xx >= policy.Consecutive5xx) return "consecutive_5xx";
                if (_windowCount >= policy.MinimumVolume && _windowFailures * 100 >= _windowCount * policy.ErrorRatePercent)
                    return "error_rate";
                return "";
            }
        }

        public void Eject(string reason)
        {
            lock (_gate)
            {
                _reason = reason;
                _ejectedUntil = DateTimeOffset.UtcNow.AddMilliseconds(policy.EjectionDurationMs);
                Array.Clear(_window);
                _windowIndex = 0;
                _windowCount = 0;
                _windowFailures = 0;
                _consecutiveFailures = 0;
                _consecutive5xx = 0;
                metrics.SetOutlierState(clusterId, destinationId, address, true, reason, _ejectedUntil.Value);
            }
        }

        public bool IsEjected
        {
            get { lock (_gate) return _ejectedUntil > DateTimeOffset.UtcNow; }
        }

        public OutlierSnapshot Snapshot()
        {
            lock (_gate)
            {
                var ejected = _ejectedUntil > DateTimeOffset.UtcNow;
                if (!ejected && _ejectedUntil != null)
                {
                    _ejectedUntil = null;
                    _reason = null;
                    metrics.SetOutlierState(clusterId, destinationId, address, false, "recovered", DateTimeOffset.UtcNow);
                }
                return new(clusterId, destinationId, address, ejected ? "ejected" : "available", _reason,
                    _ejectedUntil, _consecutiveFailures, _consecutive5xx, _windowCount, _windowFailures,
                    _windowCount == 0 ? 0 : (double)_windowFailures / _windowCount);
            }
        }
    }
}

/// <summary>
/// 从 Cluster Metadata 解析出的异常实例驱逐策略，包含连续失败、滑动窗口和驱逐比例限制。
/// </summary>
internal sealed record OutlierPolicy(
    bool Enabled, int ConsecutiveFailures, int Consecutive5xx, int WindowSize,
    int MinimumVolume, int ErrorRatePercent, int EjectionDurationMs, int MaxEjectionPercent)
{
    public static OutlierPolicy FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        const string p = "OutlierDetection.";
        if (metadata == null || !metadata.Keys.Any(key => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new(false, 5, 5, 100, 20, 50, 30000, 50);
        return new(ReadBool("Enabled", true), ReadInt("ConsecutiveFailures", 5, 1, 1000),
            ReadInt("Consecutive5xx", 5, 1, 1000), ReadInt("WindowSize", 100, 2, 10000),
            ReadInt("MinimumVolume", 20, 1, 10000), ReadInt("ErrorRatePercent", 50, 1, 100),
            ReadInt("EjectionDurationMs", 30000, 100, 3600000), ReadInt("MaxEjectionPercent", 50, 0, 100));

        bool ReadBool(string name, bool fallback) => metadata.TryGetValue(p + name, out var value)
            && bool.TryParse(value, out var parsed) ? parsed : fallback;
        int ReadInt(string name, int fallback, int min, int max) => metadata.TryGetValue(p + name, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, min, max) : fallback;
    }
}

/// <summary>
/// YARP 被动健康检查策略。每次真实代理结束后统计连接错误和 5xx，达到阈值时仅更新 Passive 健康状态，
/// 从而避免覆盖独立的 Active 健康检查结果。
/// </summary>
public sealed class OutlierDetectionPolicy(
    IDestinationHealthUpdater healthUpdater,
    OutlierDetectionRegistry registry,
    PrometheusMetrics metrics) : IPassiveHealthCheckPolicy
{
    public string Name => "LyWafOutlierDetection";

    public void RequestProxied(HttpContext context, ClusterState cluster, DestinationState destination)
    {
        var policy = OutlierPolicy.FromMetadata(cluster.Model.Config.Metadata);
        if (!policy.Enabled) return;

        var error = context.GetForwarderErrorFeature()?.Error ?? ForwarderError.None;
        var connectionFailure = error != ForwarderError.None;
        var serverError = !connectionFailure && context.Response.StatusCode >= 500;
        var entry = registry.GetOrAdd(cluster.ClusterId, destination, policy);
        var reason = entry.Record(connectionFailure, serverError);
        if (reason.Length == 0 || entry.IsEjected) return;

        var destinations = cluster.Destinations.Values;
        var maxEjected = (int)Math.Ceiling(destinations.Count * policy.MaxEjectionPercent / 100d);
        var ejected = registry.GetSnapshots().Count(x => x.ClusterId == cluster.ClusterId && x.State == "ejected");
        if (maxEjected == 0 || ejected >= maxEjected)
        {
            metrics.RecordOutlierDecision(cluster.ClusterId, destination.DestinationId, "limit_reached");
            return;
        }

        entry.Eject(reason);
        metrics.RecordOutlierDecision(cluster.ClusterId, destination.DestinationId, reason);
        healthUpdater.SetPassive(cluster, destination, DestinationHealth.Unhealthy,
            TimeSpan.FromMilliseconds(policy.EjectionDurationMs));
    }
}
