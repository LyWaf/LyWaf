using System.Collections.Concurrent;

namespace LyWaf.Shared;

/// <summary>
/// 活跃连接跟踪器
/// 分别统计 HTTP 和 WebSocket 的活跃连接数（按连接数，非唯一 IP 数）
/// </summary>
public static class ConnectionTracker
{
    // ---- HTTP 连接跟踪（每个 IP 的并发请求数）----
    private static readonly ConcurrentDictionary<string, int> _httpInFlight = new();

    // ---- WebSocket 连接跟踪（每个 IP 的并发连接数）----
    private static readonly ConcurrentDictionary<string, int> _wsInFlight = new();

    /// <summary>请求开始时调用</summary>
    public static void OnRequestStart(string clientIp, bool isWebSocket)
    {
        var dict = isWebSocket ? _wsInFlight : _httpInFlight;
        dict.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
    }

    /// <summary>请求结束时调用</summary>
    public static void OnRequestEnd(string clientIp, bool isWebSocket)
    {
        var dict = isWebSocket ? _wsInFlight : _httpInFlight;
        dict.AddOrUpdate(clientIp, 0, (_, v) => Math.Max(0, v - 1));
    }

    /// <summary>获取 HTTP 活跃连接数（所有 IP 的并发请求数之和）</summary>
    public static int GetHttpCount() => SumAndClean(_httpInFlight);

    /// <summary>获取 WebSocket 活跃连接数（所有 IP 的并发连接数之和）</summary>
    public static int GetWebSocketCount() => SumAndClean(_wsInFlight);

    /// <summary>获取总活跃连接数（HTTP + WebSocket）</summary>
    public static int GetActiveCount() => GetHttpCount() + GetWebSocketCount();

    /// <summary>累加所有连接数，顺便清理归零的条目</summary>
    private static int SumAndClean(ConcurrentDictionary<string, int> inFlight)
    {
        var total = 0;
        foreach (var kvp in inFlight)
        {
            if (kvp.Value > 0)
                total += kvp.Value;
            else
                inFlight.TryRemove(kvp.Key, out _);
        }
        return total;
    }
}
