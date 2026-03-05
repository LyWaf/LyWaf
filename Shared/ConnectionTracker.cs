using System.Collections.Concurrent;

namespace LyWaf.Shared;

/// <summary>
/// 活跃连接跟踪器
/// 统计「当前连接数」= 3 分钟内访问过的 IP ∪ 当前仍有请求在处理中的 IP
/// 独立于 AccessControlService 的连接限制功能
/// </summary>
public static class ConnectionTracker
{
    /// <summary>近期访问者有效期（分钟）</summary>
    private const int RecentMinutes = 3;

    /// <summary>每个 IP 当前正在处理的请求数</summary>
    private static readonly ConcurrentDictionary<string, int> _inFlight = new();

    /// <summary>每个 IP 最后一次请求的时间</summary>
    private static readonly ConcurrentDictionary<string, DateTime> _lastVisit = new();

    /// <summary>请求开始时调用</summary>
    public static void OnRequestStart(string clientIp)
    {
        _lastVisit[clientIp] = DateTime.UtcNow;
        _inFlight.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
    }

    /// <summary>请求结束时调用</summary>
    public static void OnRequestEnd(string clientIp)
    {
        _inFlight.AddOrUpdate(clientIp, 0, (_, v) => Math.Max(0, v - 1));
    }

    /// <summary>
    /// 获取活跃连接数（唯一 IP 数）
    /// 计算规则：最近 3 分钟有请求 或 当前有请求在处理中
    /// </summary>
    public static int GetActiveCount()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-RecentMinutes);
        var activeIps = new HashSet<string>();

        // 最近访问过的 IP
        foreach (var kvp in _lastVisit)
        {
            if (kvp.Value >= cutoff)
            {
                activeIps.Add(kvp.Key);
            }
            else
            {
                // 清理过期且无活跃连接的 IP
                if (!_inFlight.TryGetValue(kvp.Key, out var count) || count <= 0)
                    _lastVisit.TryRemove(kvp.Key, out _);
            }
        }

        // 当前仍有请求在处理中的 IP（长连接）
        foreach (var kvp in _inFlight)
        {
            if (kvp.Value > 0)
                activeIps.Add(kvp.Key);
            else
                _inFlight.TryRemove(kvp.Key, out _); // 清理已归零的
        }

        return activeIps.Count;
    }
}
