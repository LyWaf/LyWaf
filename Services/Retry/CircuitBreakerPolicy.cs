using System.Globalization;

namespace LyWaf.Services.Retry;

/// <summary>
/// Cluster 级容量保护配置，分别约束物理连接、活动请求、等待请求和并发重试。
/// </summary>
public sealed record CircuitBreakerPolicy(
    bool Enabled,
    int MaxConnections,
    int MaxActiveRequests,
    int MaxQueuedRequests,
    int MaxConcurrentRetries)
{
    public static CircuitBreakerPolicy FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        const string prefix = "CircuitBreaker.";
        if (metadata == null || !metadata.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return new(false, 200, int.MaxValue, 0, int.MaxValue);

        return new(
            ReadBool(metadata, prefix + "Enabled", true),
            Math.Clamp(ReadInt(metadata, prefix + "MaxConnections", 200), 1, 100_000),
            Math.Clamp(ReadInt(metadata, prefix + "MaxActiveRequests", 1000), 1, 100_000),
            Math.Clamp(ReadInt(metadata, prefix + "MaxQueuedRequests", 1000), 0, 100_000),
            Math.Clamp(ReadInt(metadata, prefix + "MaxConcurrentRetries", 100), 0, 100_000));
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}

/// <summary>
/// 单个 YARP Cluster HTTP 客户端共享的容量计数器，负责申请和释放请求及重试槽位。
/// </summary>
internal sealed class CircuitBreakerState(string clusterId, CircuitBreakerPolicy policy, Metrics.PrometheusMetrics metrics)
{
    private readonly SemaphoreSlim _activeSlots = new(policy.MaxActiveRequests, policy.MaxActiveRequests);
    private int _active;
    private int _queued;
    private int _retries;

    public async ValueTask<bool> EnterAsync(CancellationToken cancellationToken)
    {
        if (!policy.Enabled) return true;
        if (!_activeSlots.Wait(0))
        {
            var queued = Interlocked.Increment(ref _queued);
            Publish("saturated");
            if (queued > policy.MaxQueuedRequests)
            {
                Interlocked.Decrement(ref _queued);
                metrics.RecordCircuitBreakerRejection(clusterId, "queue_full");
                Publish("open");
                return false;
            }
            try { await _activeSlots.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref _queued); }
        }
        Interlocked.Increment(ref _active);
        Publish("closed");
        return true;
    }

    public void Exit()
    {
        if (!policy.Enabled) return;
        Interlocked.Decrement(ref _active);
        _activeSlots.Release();
        Publish("closed");
    }

    public bool TryEnterRetry()
    {
        if (!policy.Enabled) return true;
        while (true)
        {
            var current = Volatile.Read(ref _retries);
            if (current >= policy.MaxConcurrentRetries)
            {
                metrics.RecordCircuitBreakerRejection(clusterId, "retry_limit");
                Publish("saturated");
                return false;
            }
            if (Interlocked.CompareExchange(ref _retries, current + 1, current) == current)
            {
                Publish("closed");
                return true;
            }
        }
    }

    public void ExitRetry()
    {
        if (!policy.Enabled) return;
        Interlocked.Decrement(ref _retries);
        Publish("closed");
    }

    private void Publish(string state) => metrics.SetCircuitBreakerState(clusterId, state,
        Math.Max(0, policy.MaxActiveRequests - Volatile.Read(ref _active)),
        Volatile.Read(ref _queued), Volatile.Read(ref _active), Volatile.Read(ref _retries));
}
