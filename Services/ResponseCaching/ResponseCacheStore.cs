using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.ResponseCaching;

/// <summary>
/// 管理响应缓存的内存索引、单键回源锁和磁盘正文生命周期。
/// 服务启动时删除孤立临时文件，不读取正文内容到内存。
/// </summary>
public sealed class ResponseCacheStore : IDisposable
{
    private readonly ConcurrentDictionary<string, ResponseCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheLockState> _locks = new(StringComparer.Ordinal);
    private readonly object _lockGate = new();
    private readonly object _mutationGate = new();
    private readonly IOptionsMonitor<ResponseCacheOptions> _options;
    private readonly PrometheusMetrics? _metrics;
    private string _directory;
    private long _totalBytes;

    public ResponseCacheStore(IOptionsMonitor<ResponseCacheOptions> options, PrometheusMetrics? metrics = null)
    {
        _options = options;
        _metrics = metrics;
        _directory = ResolveDirectory(options.CurrentValue.Directory);
        Directory.CreateDirectory(_directory);
        DeleteOrphans(_directory);
    }

    internal static string BaseKey(HttpRequest request)
        => $"{(request.Host.Value ?? string.Empty).ToLowerInvariant()}\n{request.PathBase}{request.Path}{request.QueryString}";

    internal static string VariantKey(string baseKey, IReadOnlyDictionary<string, string> varyValues)
    {
        var canonical = new StringBuilder(baseKey);
        foreach (var pair in varyValues.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            canonical.Append('\n').Append(pair.Key.ToLowerInvariant()).Append(':').Append(pair.Value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    internal bool TryFind(HttpRequest request, out ResponseCacheEntry? entry)
    {
        var baseKey = BaseKey(request);
        foreach (var candidate in _entries.Values.Where(x => x.BaseKey == baseKey))
        {
            if (!candidate.VaryHeaders.All(name =>
                    string.Equals(candidate.VaryValues.GetValueOrDefault(name, ""), request.Headers[name].ToString(), StringComparison.Ordinal)))
                continue;
            if (!File.Exists(candidate.BodyPath))
            {
                Remove(candidate.Key);
                continue;
            }
            candidate.Touch(DateTimeOffset.UtcNow);
            entry = candidate;
            return true;
        }
        entry = null;
        return false;
    }

    internal async ValueTask<IDisposable> AcquireLockAsync(string baseKey, CancellationToken cancellationToken)
    {
        CacheLockState state;
        lock (_lockGate)
        {
            if (!_locks.TryGetValue(baseKey, out state!))
                _locks[baseKey] = state = new CacheLockState();
            state.References++;
        }

        try
        {
            await state.Semaphore.WaitAsync(cancellationToken);
            return new CacheLockLease(this, baseKey, state);
        }
        catch
        {
            ReleaseLockReference(baseKey, state, releaseSemaphore: false);
            throw;
        }
    }

    internal int ActiveLockCount
    {
        get { lock (_lockGate) return _locks.Count; }
    }

    internal string CreateTemporaryPath()
    {
        RefreshDirectory();
        return Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
    }

    internal ResponseCacheEntry? Commit(string temporaryPath, string baseKey, HttpRequest request, int statusCode,
        IReadOnlyDictionary<string, string[]> headers, IReadOnlyList<string> varyHeaders, TimeSpan ttl,
        TimeSpan staleWhileRevalidate, TimeSpan staleIfError, string? etag, DateTimeOffset? lastModified)
    {
        var varyValues = varyHeaders.ToDictionary(x => x, x => request.Headers[x].ToString(), StringComparer.OrdinalIgnoreCase);
        var key = VariantKey(baseKey, varyValues);
        var destination = Path.Combine(_directory, $"{key}.body");
        var now = DateTimeOffset.UtcNow;
        lock (_mutationGate)
        {
            var length = new FileInfo(temporaryPath).Length;
            if (length > Math.Max(0, _options.CurrentValue.MaxCacheBytes)) return null;
            if (_entries.TryRemove(key, out var previous))
            {
                Interlocked.Add(ref _totalBytes, -previous.Length);
                DeleteFile(previous.BodyPath);
            }
            File.Move(temporaryPath, destination, true);
            var entry = new ResponseCacheEntry(key, baseKey, request.Host.Value ?? string.Empty, $"{request.PathBase}{request.Path}{request.QueryString}",
                statusCode, destination, length, now, now + ttl, now + ttl + staleWhileRevalidate,
                now + ttl + staleIfError, headers, varyHeaders, varyValues, etag, lastModified);
            _entries[key] = entry;
            Interlocked.Add(ref _totalBytes, length);
            EvictIfNeeded();
            UpdateMetrics();
            return entry;
        }
    }

    internal void Refresh(ResponseCacheEntry entry, TimeSpan ttl, TimeSpan staleWhileRevalidate, TimeSpan staleIfError)
    {
        var now = DateTimeOffset.UtcNow;
        _entries[entry.Key] = entry with
        {
            StoredAt = now,
            FreshUntil = now + ttl,
            StaleWhileRevalidateUntil = now + ttl + staleWhileRevalidate,
            StaleIfErrorUntil = now + ttl + staleIfError
        };
    }

    /// <summary>返回所有缓存条目的元数据快照，正文不会被读取。</summary>
    public IReadOnlyList<ResponseCacheEntrySnapshot> GetSnapshots() => _entries.Values
        .Select(x => x.Snapshot()).OrderByDescending(x => x.LastAccessedAt).ToArray();

    /// <summary>按精确 Key 或 Host/Path 前缀删除条目，返回删除数量。</summary>
    public int Purge(PurgeResponseCacheRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Key) && string.IsNullOrWhiteSpace(request.Host) && string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("Key、Host、Path 至少提供一个");
        var matches = _entries.Values.Where(entry =>
            (string.IsNullOrWhiteSpace(request.Key) || entry.Key.Equals(request.Key, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(request.Host) || entry.Host.Equals(request.Host, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(request.Path) || entry.Path.StartsWith(request.Path, StringComparison.Ordinal))).ToArray();
        foreach (var entry in matches) Remove(entry.Key);
        return matches.Length;
    }

    private void Remove(string key)
    {
        if (!_entries.TryRemove(key, out var entry)) return;
        Interlocked.Add(ref _totalBytes, -entry.Length);
        DeleteFile(entry.BodyPath);
        UpdateMetrics();
    }

    private void EvictIfNeeded()
    {
        var limit = Math.Max(0, _options.CurrentValue.MaxCacheBytes);
        while (Interlocked.Read(ref _totalBytes) > limit)
        {
            var oldest = _entries.Values.OrderBy(x => x.LastAccessedAt).FirstOrDefault();
            if (oldest is null) break;
            Remove(oldest.Key);
        }
    }

    private void RefreshDirectory()
    {
        var current = ResolveDirectory(_options.CurrentValue.Directory);
        if (current == _directory) return;
        lock (_mutationGate)
        {
            if (current == _directory) return;
            foreach (var key in _entries.Keys) Remove(key);
            _directory = current;
            Directory.CreateDirectory(_directory);
            DeleteOrphans(_directory);
        }
    }

    private static string ResolveDirectory(string path) => Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "datas/response-cache" : path);
    private void UpdateMetrics() => _metrics?.SetResponseCacheSize(_entries.Count, Interlocked.Read(ref _totalBytes));
    private static void DeleteOrphans(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory)) DeleteFile(file);
    }
    internal static void DeleteFile(string path) { try { File.Delete(path); } catch { } }

    private void ReleaseLockReference(string baseKey, CacheLockState state, bool releaseSemaphore)
    {
        lock (_lockGate)
        {
            if (releaseSemaphore) state.Semaphore.Release();
            state.References--;
            if (state.References != 0 || !_locks.Remove(baseKey)) return;
            state.Semaphore.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lockGate)
        {
            foreach (var state in _locks.Values) state.Semaphore.Dispose();
            _locks.Clear();
        }
    }

    private sealed class CacheLockState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class CacheLockLease(ResponseCacheStore owner, string baseKey, CacheLockState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseLockReference(baseKey, state, releaseSemaphore: true);
        }
    }
}
