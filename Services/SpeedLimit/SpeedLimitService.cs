using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.SpeedLimit;


public interface ISpeedLimitService
{
    public PartitionedRateLimiter<HttpContext>? Get(string key);

    public int GetRejectCode();

    public SpeedLimitOptions GetOptions();

    /// <summary>
    /// 检查IP是否被允许访问
    /// </summary>
    /// <param name="clientIp">客户端IP</param>
    /// <param name="path">请求路径（可选，用于路径级别的访问控制）</param>
    /// <returns>如果允许访问返回true，否则返回false</returns>
    public bool IsIpAllowed(string clientIp, string? path = null);

    /// <summary>
    /// 尝试获取连接许可
    /// </summary>
    /// <param name="clientIp">客户端IP</param>
    /// <param name="destination">目标服务器地址</param>
    /// <param name="path">请求路径</param>
    /// <returns>如果获取成功返回true，否则返回false</returns>
    public bool TryAcquireConnection(string clientIp, string? destination = null, string? path = null);

    /// <summary>
    /// 释放连接许可
    /// </summary>
    /// <param name="clientIp">客户端IP</param>
    /// <param name="destination">目标服务器地址</param>
    /// <param name="path">请求路径</param>
    public void ReleaseConnection(string clientIp, string? destination = null, string? path = null);

    /// <summary>
    /// 获取当前连接统计信息
    /// </summary>
    public ConnectionStats GetConnectionStats();
}


/// <summary>
/// 连接统计信息
/// </summary>
public class ConnectionStats
{
    public int TotalConnections { get; set; }
    public Dictionary<string, int> ConnectionsPerIp { get; set; } = [];
    public Dictionary<string, int> ConnectionsPerDestination { get; set; } = [];
    public Dictionary<string, int> ConnectionsPerPath { get; set; } = [];
}

public class SpeedLimitService : ISpeedLimitService
{
    private SpeedLimitOptions _options;
    private readonly IMemoryCache _cache;
    private static readonly NLog.Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<string, PartitionedRateLimiter<HttpContext>> _allRateLimiter = [];

    // IP访问控制缓存
    private List<IpNetwork> _whitelistNetworks = [];
    private List<IpNetwork> _blacklistNetworks = [];
    private Dictionary<string, (List<IpNetwork> Allow, List<IpNetwork> Deny)> _pathRuleNetworks = [];

    // 连接计数器
    private int _totalConnections = 0;
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new();
    private readonly ConcurrentDictionary<string, int> _connectionsPerDestination = new();
    private readonly ConcurrentDictionary<string, int> _connectionsPerPath = new();
    private readonly object _connectionLock = new();

    public SpeedLimitService(
        IOptionsMonitor<SpeedLimitOptions> options, IConfiguration configuration, IMemoryCache cache
        )
    {
        _options = options.CurrentValue;
        // 可以订阅变更，但需注意生命周期和内存泄漏
        options.OnChange(newConfig =>
        {
            _options = newConfig;
            BuildPartitioned();
            BuildAccessControlNetworks();
        });
        BuildPartitioned();
        BuildAccessControlNetworks();
        _cache = cache;
    }

    /// <summary>
    /// 构建IP访问控制网络列表
    /// </summary>
    private void BuildAccessControlNetworks()
    {
        var accessControl = _options.AccessControl;

        // 解析白名单
        _whitelistNetworks = ParseNetworks(accessControl.Whitelist);

        // 解析黑名单
        _blacklistNetworks = ParseNetworks(accessControl.Blacklist);

        // 解析路径规则
        _pathRuleNetworks = [];
        foreach (var rule in accessControl.PathRules)
        {
            var allowNetworks = ParseNetworks(rule.Value.Allow);
            var denyNetworks = ParseNetworks(rule.Value.Deny);
            _pathRuleNetworks[rule.Key] = (allowNetworks, denyNetworks);
        }

        _logger.Info("访问控制规则已更新: 白名单{WhiteCount}条, 黑名单{BlackCount}条, 路径规则{PathCount}条",
            _whitelistNetworks.Count, _blacklistNetworks.Count, _pathRuleNetworks.Count);
    }

    private static List<IpNetwork> ParseNetworks(List<string> cidrs)
    {
        var networks = new List<IpNetwork>();
        foreach (var cidr in cidrs)
        {
            if (IpNetwork.TryParse(cidr.Trim(), out var network) && network != null)
            {
                networks.Add(network);
            }
        }
        return networks;
    }

    /// <summary>
    /// 检查IP是否在网络列表中
    /// </summary>
    private static bool IsIpInNetworks(string clientIp, List<IpNetwork> networks)
    {
        if (string.IsNullOrEmpty(clientIp) || networks.Count == 0)
            return false;

        foreach (var network in networks)
        {
            if (network.Contains(clientIp))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 检查IP是否被允许访问
    /// </summary>
    public bool IsIpAllowed(string clientIp, string? path = null)
    {
        var accessControl = _options.AccessControl;

        // 未启用访问控制，允许所有
        if (!accessControl.Enabled)
            return true;

        // 首先检查路径级别的规则
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var rule in _pathRuleNetworks)
            {
                if (MatchPath(path, rule.Key))
                {
                    // 路径规则的deny列表优先
                    if (IsIpInNetworks(clientIp, rule.Value.Deny))
                        return false;

                    // 如果在allow列表中，允许访问
                    if (IsIpInNetworks(clientIp, rule.Value.Allow))
                        return true;
                }
            }
        }

        // 根据模式判断
        if (accessControl.Mode == AccessControlMode.Whitelist)
        {
            // 白名单中的IP
            if (IsIpInNetworks(clientIp, _whitelistNetworks))
                return true;
            return false;
        }
        else
        {
            // 黑名单模式：黑名单中的IP被拒绝
            return !IsIpInNetworks(clientIp, _blacklistNetworks);
        }
    }

    /// <summary>
    /// 匹配路径，支持通配符
    /// </summary>
    private static bool MatchPath(string path, string pattern)
    {
        if (pattern.EndsWith("/*"))
        {
            var prefix = pattern[..^2];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        else if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 尝试获取连接许可
    /// </summary>
    public bool TryAcquireConnection(string clientIp, string? destination = null, string? path = null)
    {
        var connectionLimit = _options.ConnectionLimit;

        if (!connectionLimit.Enabled)
            return true;

        lock (_connectionLock)
        {
            // 检查全局连接数
            if (connectionLimit.MaxTotalConnections > 0 && _totalConnections >= connectionLimit.MaxTotalConnections)
            {
                _logger.Warn("全局连接数已达上限: {Current}/{Max}", _totalConnections, connectionLimit.MaxTotalConnections);
                return false;
            }

            // 检查每个IP的连接数
            if (connectionLimit.MaxConnectionsPerIp > 0 && !string.IsNullOrEmpty(clientIp))
            {
                var ipConnections = _connectionsPerIp.GetValueOrDefault(clientIp, 0);
                if (ipConnections >= connectionLimit.MaxConnectionsPerIp)
                {
                    _logger.Warn("IP {ClientIp} 连接数已达上限: {Current}/{Max}", clientIp, ipConnections, connectionLimit.MaxConnectionsPerIp);
                    return false;
                }
            }

            // 检查每个后端服务器的连接数
            if (connectionLimit.MaxConnectionsPerDestination > 0 && !string.IsNullOrEmpty(destination))
            {
                var destConnections = _connectionsPerDestination.GetValueOrDefault(destination, 0);
                if (destConnections >= connectionLimit.MaxConnectionsPerDestination)
                {
                    _logger.Warn("目标 {Destination} 连接数已达上限: {Current}/{Max}", destination, destConnections, connectionLimit.MaxConnectionsPerDestination);
                    return false;
                }
            }

            // 检查路径连接数
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var pathLimit in connectionLimit.PathLimits)
                {
                    if (MatchPath(path, pathLimit.Key))
                    {
                        var pathConnections = _connectionsPerPath.GetValueOrDefault(pathLimit.Key, 0);
                        if (pathConnections >= pathLimit.Value)
                        {
                            _logger.Warn("路径 {Path} 连接数已达上限: {Current}/{Max}", path, pathConnections, pathLimit.Value);
                            return false;
                        }
                        // 递增路径连接数
                        _connectionsPerPath.AddOrUpdate(pathLimit.Key, 1, (_, v) => v + 1);
                        break;
                    }
                }
            }

            // 递增连接计数
            _totalConnections++;
            if (!string.IsNullOrEmpty(clientIp))
            {
                _connectionsPerIp.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
            }
            if (!string.IsNullOrEmpty(destination))
            {
                _connectionsPerDestination.AddOrUpdate(destination, 1, (_, v) => v + 1);
            }

            return true;
        }
    }

    /// <summary>
    /// 释放连接许可
    /// </summary>
    public void ReleaseConnection(string clientIp, string? destination = null, string? path = null)
    {
        var connectionLimit = _options.ConnectionLimit;

        if (!connectionLimit.Enabled)
            return;

        lock (_connectionLock)
        {
            // 递减全局连接数
            if (_totalConnections > 0)
                _totalConnections--;

            // 递减IP连接数
            if (!string.IsNullOrEmpty(clientIp))
            {
                _connectionsPerIp.AddOrUpdate(clientIp, 0, (_, v) => Math.Max(0, v - 1));
            }

            // 递减目标服务器连接数
            if (!string.IsNullOrEmpty(destination))
            {
                _connectionsPerDestination.AddOrUpdate(destination, 0, (_, v) => Math.Max(0, v - 1));
            }

            // 递减路径连接数
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var pathLimit in connectionLimit.PathLimits)
                {
                    if (MatchPath(path, pathLimit.Key))
                    {
                        _connectionsPerPath.AddOrUpdate(pathLimit.Key, 0, (_, v) => Math.Max(0, v - 1));
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 获取连接统计信息
    /// </summary>
    public ConnectionStats GetConnectionStats()
    {
        return new ConnectionStats
        {
            TotalConnections = _totalConnections,
            ConnectionsPerIp = new Dictionary<string, int>(_connectionsPerIp),
            ConnectionsPerDestination = new Dictionary<string, int>(_connectionsPerDestination),
            ConnectionsPerPath = new Dictionary<string, int>(_connectionsPerPath)
        };
    }

    private static string BuildPartitionKey(HttpContext httpContext, string? paritition)
    {

        if (paritition == null)
        {
            return "all";
        }
        return "all";
    }

    private void BuildPartitioned()
    {
        foreach (var limit in _options.Limits)
        {
            switch (limit.Value.Name)
            {
                case "Fixed":
                    {
                        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                        {
                            return RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: BuildPartitionKey(httpContext, limit.Value.Partition),
                                factory: _ => new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = limit.Value.PermitLimit,
                                    Window = limit.Value.Window,
                                    QueueProcessingOrder = limit.Value.QueueOrder,
                                    QueueLimit = limit.Value.QueueLimit,
                                });
                        });
                        _allRateLimiter[limit.Key] = limiter;
                        break;
                    }
                case "Sliding":
                    {
                        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                        {
                            return RateLimitPartition.GetSlidingWindowLimiter(
                                partitionKey: BuildPartitionKey(httpContext, limit.Value.Partition),
                                factory: _ => new SlidingWindowRateLimiterOptions
                                {
                                    PermitLimit = limit.Value.PermitLimit,
                                    Window = limit.Value.Window,
                                    SegmentsPerWindow = limit.Value.SegmentsPerWindow,
                                    QueueProcessingOrder = limit.Value.QueueOrder,
                                    QueueLimit = limit.Value.QueueLimit,
                                });
                        });
                        _allRateLimiter[limit.Key] = limiter;
                        break;
                    }
                case "Token":
                    {
                        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                        {
                            return RateLimitPartition.GetTokenBucketLimiter(
                                partitionKey: BuildPartitionKey(httpContext, limit.Value.Partition),
                                factory: _ => new TokenBucketRateLimiterOptions
                                {
                                    TokenLimit = limit.Value.PermitLimit,
                                    ReplenishmentPeriod = limit.Value.ReplenishmentPeriod ?? TimeSpan.FromSeconds(10),
                                    TokensPerPeriod = limit.Value.TokensPerPeriod,
                                    QueueProcessingOrder = limit.Value.QueueOrder,
                                    QueueLimit = limit.Value.QueueLimit,
                                });
                        });
                        _allRateLimiter[limit.Key] = limiter;
                        break;
                    }

                case "Concurrency":
                    {
                        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                        {
                            return RateLimitPartition.GetConcurrencyLimiter(
                                partitionKey: BuildPartitionKey(httpContext, limit.Value.Partition),
                                factory: _ => new ConcurrencyLimiterOptions
                                {
                                    PermitLimit = limit.Value.PermitLimit,
                                    QueueProcessingOrder = limit.Value.QueueOrder,
                                    QueueLimit = limit.Value.QueueLimit,
                                });
                        });
                        _allRateLimiter[limit.Key] = limiter;
                        break;
                    }
                default:
                    {
                        throw new Exception($"unsupport type {limit.Value.Name}, Only support Fixed, Sliding, Token, Concurrency");
                    }
            }
        }
    }

    public PartitionedRateLimiter<HttpContext>? Get(string key)
    {
        if (_allRateLimiter.TryGetValue(key, out var val))
        {
            return val;
        }
        if (_options.Default != null)
        {
            if (_allRateLimiter.TryGetValue(_options.Default, out val))
            {
                return val;
            }
        }
        return null;
    }


    public int GetRejectCode()
    {
        return _options.RejectCode;
    }

    public SpeedLimitOptions GetOptions()
    {
        return _options;
    }
}