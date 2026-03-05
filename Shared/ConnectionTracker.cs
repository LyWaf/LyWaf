using System.Collections.Concurrent;

namespace LyWaf.Shared;

/// <summary>
/// 活跃连接跟踪器
/// 分别统计 HTTP 和 WebSocket 的活跃连接数
/// 活跃 = 3 分钟内访问过的 IP ∪ 当前仍有请求在处理中的 IP
/// </summary>
public static class ConnectionTracker
{
    /// <summary>近期访问者有效期（分钟）</summary>
    private const int RecentMinutes = 3;

    // ---- HTTP 连接跟踪 ----
    private static readonly ConcurrentDictionary<string, int> _httpInFlight = new();
    private static readonly ConcurrentDictionary<string, DateTime> _httpLastVisit = new();

    // ---- WebSocket 连接跟踪 ----
    private static readonly ConcurrentDictionary<string, int> _wsInFlight = new();
    private static readonly ConcurrentDictionary<string, DateTime> _wsLastVisit = new();

    /// <summary>请求开始时调用</summary>
    public static void OnRequestStart(string clientIp, bool isWebSocket)
    {
        if (isWebSocket)
        {
            _wsLastVisit[clientIp] = DateTime.UtcNow;
            _wsInFlight.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
        }
        else
        {
            _httpLastVisit[clientIp] = DateTime.UtcNow;
            _httpInFlight.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
        }
    }

    /// <summary>请求结束时调用</summary>
    public static void OnRequestEnd(string clientIp, bool isWebSocket)
    {
        if (isWebSocket)
        {
            _wsInFlight.AddOrUpdate(clientIp, 0, (_, v) => Math.Max(0, v - 1));
        }
        else
        {
            _httpInFlight.AddOrUpdate(clientIp, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    /// <summary>获取 HTTP 活跃连接数</summary>
    public static int GetHttpCount()
    {
        return GetCount(_httpInFlight, _httpLastVisit);
    }

    /// <summary>获取 WebSocket 活跃连接数</summary>
    public static int GetWebSocketCount()
    {
        return GetCount(_wsInFlight, _wsLastVisit);
    }

    /// <summary>获取总活跃连接数（HTTP + WebSocket）</summary>
    public static int GetActiveCount()
    {
        return GetHttpCount() + GetWebSocketCount();
    }

    private static int GetCount(
        ConcurrentDictionary<string, int> inFlight,
        ConcurrentDictionary<string, DateTime> lastVisit)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-RecentMinutes);
        var activeIps = new HashSet<string>();

        // 最近访问过的 IP
        foreach (var kvp in lastVisit)
        {
            if (kvp.Value >= cutoff)
            {
                activeIps.Add(kvp.Key);
            }
            else
            {
                // 清理过期且无活跃连接的 IP
                if (!inFlight.TryGetValue(kvp.Key, out var count) || count <= 0)
                    lastVisit.TryRemove(kvp.Key, out _);
            }
        }

        // 当前仍有请求在处理中的 IP（长连接）
        foreach (var kvp in inFlight)
        {
            if (kvp.Value > 0)
                activeIps.Add(kvp.Key);
            else
                inFlight.TryRemove(kvp.Key, out _);
        }

        return activeIps.Count;
    }
}
