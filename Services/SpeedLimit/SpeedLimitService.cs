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
    /// 获取带宽限速配置
    /// </summary>
    ThrottleConfig GetThrottleConfig();

    /// <summary>
    /// 设置 IP 带宽限速
    /// </summary>
    void SetIpThrottle(string ip, int limitKbps);

    /// <summary>
    /// 移除 IP 带宽限速
    /// </summary>
    bool RemoveIpThrottle(string ip);

    /// <summary>
    /// 设置路径带宽限速
    /// </summary>
    void SetPathThrottle(string path, int limitKbps);

    /// <summary>
    /// 移除路径带宽限速
    /// </summary>
    bool RemovePathThrottle(string path);

    /// <summary>
    /// 设置全局带宽限速
    /// </summary>
    void SetGlobalThrottle(int limitKbps);
}

/// <summary>
/// 带宽限速配置
/// </summary>
public class ThrottleConfig
{
    public int Global { get; set; }
    public Dictionary<string, int> PathLimits { get; set; } = [];
    public Dictionary<string, int> IpLimits { get; set; } = [];
}

public class SpeedLimitService : ISpeedLimitService
{
    private SpeedLimitOptions _options;
    private readonly IMemoryCache _cache;
    private static readonly NLog.Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<string, PartitionedRateLimiter<HttpContext>> _allRateLimiter = [];
    
    // 动态添加的带宽限速规则
    private readonly Dictionary<string, int> _dynamicIpThrottle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _dynamicPathThrottle = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _throttleLock = new();

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
        });
        BuildPartitioned();
        _cache = cache;
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

    /// <summary>
    /// 获取带宽限速配置
    /// </summary>
    public ThrottleConfig GetThrottleConfig()
    {
        lock (_throttleLock)
        {
            var pathLimits = new Dictionary<string, int>(_options.Throttled.Everys);
            foreach (var kv in _dynamicPathThrottle)
            {
                pathLimits[kv.Key] = kv.Value;
            }

            var ipLimits = new Dictionary<string, int>(_options.Throttled.IpEverys);
            foreach (var kv in _dynamicIpThrottle)
            {
                ipLimits[kv.Key] = kv.Value;
            }

            return new ThrottleConfig
            {
                Global = _options.Throttled.Global,
                PathLimits = pathLimits,
                IpLimits = ipLimits
            };
        }
    }

    /// <summary>
    /// 设置 IP 带宽限速
    /// </summary>
    public void SetIpThrottle(string ip, int limitKbps)
    {
        lock (_throttleLock)
        {
            _dynamicIpThrottle[ip.Trim()] = limitKbps;
            _logger.Info("已设置 IP {Ip} 带宽限速: {Limit} KB/s", ip, limitKbps);
        }
    }

    /// <summary>
    /// 移除 IP 带宽限速
    /// </summary>
    public bool RemoveIpThrottle(string ip)
    {
        var trimmed = ip.Trim();
        var removed = false;
        lock (_throttleLock)
        {
            removed |= _dynamicIpThrottle.Remove(trimmed);
            removed |= _options.Throttled.IpEverys.Remove(trimmed);
        }
        if (removed)
            _logger.Info("已移除 IP {Ip} 的带宽限速", ip);
        return removed;
    }

    /// <summary>
    /// 设置路径带宽限速
    /// </summary>
    public void SetPathThrottle(string path, int limitKbps)
    {
        lock (_throttleLock)
        {
            _dynamicPathThrottle[path.Trim()] = limitKbps;
            _logger.Info("已设置路径 {Path} 带宽限速: {Limit} KB/s", path, limitKbps);
        }
    }

    /// <summary>
    /// 移除路径带宽限速
    /// </summary>
    public bool RemovePathThrottle(string path)
    {
        var trimmed = path.Trim();
        var removed = false;
        lock (_throttleLock)
        {
            removed |= _dynamicPathThrottle.Remove(trimmed);
            removed |= _options.Throttled.Everys.Remove(trimmed);
        }
        if (removed)
            _logger.Info("已移除路径 {Path} 的带宽限速", path);
        return removed;
    }

    /// <summary>
    /// 获取动态 IP 带宽限速（供 Throttled 中间件使用）
    /// </summary>
    public int? GetDynamicIpLimit(string ip)
    {
        lock (_throttleLock)
        {
            if (_dynamicIpThrottle.TryGetValue(ip, out var limit))
                return limit;
        }
        return null;
    }

    /// <summary>
    /// 获取动态路径带宽限速（供 Throttled 中间件使用）
    /// </summary>
    public int? GetDynamicPathLimit(string path)
    {
        lock (_throttleLock)
        {
            foreach (var kv in _dynamicPathThrottle)
            {
                if (path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    (kv.Key.EndsWith("*") && path.StartsWith(kv.Key[..^1], StringComparison.OrdinalIgnoreCase)))
                {
                    return kv.Value;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 设置全局带宽限速（直接修改运行时选项）
    /// </summary>
    public void SetGlobalThrottle(int limitKbps)
    {
        _options.Throttled.Global = limitKbps;
        _logger.Info("已设置全局带宽限速: {Limit} KB/s", limitKbps);
    }
}
