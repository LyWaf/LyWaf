using System.Collections.Concurrent;

namespace LyWaf.Services.ExternalAuthorization;

/// <summary>有界、进程内的外部授权结果缓存；键中的身份信息已由调用方哈希。</summary>
public sealed class ExternalAuthorizationCache
{
    private readonly ConcurrentDictionary<string, CachedAuthorization> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, DateTimeOffset now, out ExternalAuthorizationResponse response)
    {
        if (_entries.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            response = cached.Response;
            return true;
        }
        _entries.TryRemove(key, out _);
        response = null!;
        return false;
    }

    public void Set(string key, ExternalAuthorizationResponse response, DateTimeOffset now, int seconds, int maxEntries)
    {
        if (seconds <= 0) return;
        var limit = Math.Clamp(maxEntries, 1, 100_000);
        if (_entries.Count >= limit)
        {
            foreach (var expired in _entries.Where(item => item.Value.ExpiresAt <= now).Take(256))
                _entries.TryRemove(expired.Key, out _);
            if (_entries.Count >= limit)
                _entries.TryRemove(_entries.OrderBy(static item => item.Value.ExpiresAt).First().Key, out _);
        }
        _entries[key] = new CachedAuthorization(response, now.AddSeconds(seconds));
    }
}
