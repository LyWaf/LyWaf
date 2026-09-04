using System.Collections.Concurrent;

namespace LyWaf.Services.ManagementApi;

/// <summary>为管理 API 提供有界来源跟踪和固定窗口频率准入。</summary>
public sealed class ManagementApiGuard(Microsoft.Extensions.Options.IOptionsMonitor<ManagementApiOptions> options)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Window _overflow = new();
    private long _operations;

    /// <summary>尝试取得本分钟的读或写请求配额。</summary>
    public bool TryAcquire(string identity, bool write, DateTimeOffset now, out int retryAfterSeconds)
    {
        var current = options.CurrentValue;
        var key = (write ? "w:" : "r:") + identity;
        Window window;
        if (!_windows.TryGetValue(key, out window!))
        {
            window = _windows.Count >= Math.Clamp(current.MaxTrackedClients, 100, 100_000)
                ? _overflow
                : _windows.GetOrAdd(key, static _ => new Window());
        }
        var limit = write ? current.WriteRequestsPerMinute : current.ReadRequestsPerMinute;
        var allowed = window.TryAcquire(now, Math.Clamp(limit, 1, 100_000), out retryAfterSeconds);
        if ((Interlocked.Increment(ref _operations) & 0xff) == 0) Cleanup(now.AddMinutes(-2));
        return allowed;
    }

    private void Cleanup(DateTimeOffset cutoff)
    {
        foreach (var pair in _windows)
            if (pair.Value.LastSeen < cutoff) _windows.TryRemove(pair);
    }

    private sealed class Window
    {
        private readonly object _gate = new();
        private long _minute;
        private int _count;
        private long _lastSeen;
        public DateTimeOffset LastSeen => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _lastSeen));

        public bool TryAcquire(DateTimeOffset now, int limit, out int retryAfterSeconds)
        {
            var seconds = now.ToUnixTimeSeconds();
            var minute = seconds / 60;
            lock (_gate)
            {
                _lastSeen = seconds;
                if (_minute != minute) { _minute = minute; _count = 0; }
                if (_count >= limit)
                {
                    retryAfterSeconds = Math.Max(1, 60 - (int)(seconds % 60));
                    return false;
                }
                _count++;
                retryAfterSeconds = 0;
                return true;
            }
        }
    }
}
