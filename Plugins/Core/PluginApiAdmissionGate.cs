using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Core;

/// <summary>插件代理 API 的准入结果，包含稳定错误码及可选重试秒数。</summary>
public sealed record PluginApiAdmission(bool Allowed, string? ErrorCode = null, string? Message = null, int? RetryAfterSeconds = null, IDisposable? Lease = null);

/// <summary>限制插件代理 API 的请求体、每用户调用频率和全局并发数。</summary>
public sealed class PluginApiAdmissionGate : IDisposable
{
    private readonly IOptionsMonitor<PluginOptions> _options;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _requests = new(StringComparer.Ordinal);
    private SemaphoreSlim _concurrency;
    private int _concurrencyLimit;

    public PluginApiAdmissionGate(IOptionsMonitor<PluginOptions> options)
    {
        _options = options;
        _concurrencyLimit = Math.Max(1, options.CurrentValue.ApiProxy.MaxConcurrentRequests);
        _concurrency = new SemaphoreSlim(_concurrencyLimit, _concurrencyLimit);
    }

    /// <summary>尝试取得一次插件代理调用租约；调用方必须释放成功返回的 Lease。</summary>
    public PluginApiAdmission TryAcquire(string identity, string pluginId, long? contentLength)
    {
        var options = _options.CurrentValue.ApiProxy;
        if (contentLength > options.MaxRequestBodyBytes)
            return new(false, "plugin_request_too_large", $"插件请求体不能超过 {options.MaxRequestBodyBytes} 字节");

        EnsureConcurrencyLimit(options.MaxConcurrentRequests);
        if (!_concurrency.Wait(0))
            return new(false, "plugin_concurrency_limited", "插件 API 当前并发请求过多", 1);

        var key = identity + "\n" + pluginId;
        var queue = _requests.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1)) queue.Dequeue();
            if (queue.Count >= Math.Max(1, options.RequestsPerMinute))
            {
                _concurrency.Release();
                var retry = Math.Max(1, (int)Math.Ceiling((queue.Peek().AddMinutes(1) - now).TotalSeconds));
                return new(false, "plugin_rate_limited", "插件 API 每分钟请求数超过限制", retry);
            }
            queue.Enqueue(now);
        }
        return new(true, Lease: new ReleaseLease(_concurrency));
    }

    private void EnsureConcurrencyLimit(int requested)
    {
        requested = Math.Max(1, requested);
        if (requested == _concurrencyLimit) return;
        // 配置变化只影响后续租约；已有租约继续使用旧信号量并安全释放。
        Interlocked.Exchange(ref _concurrency, new SemaphoreSlim(requested, requested));
        _concurrencyLimit = requested;
    }

    public void Dispose() => _concurrency.Dispose();

    private sealed class ReleaseLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
