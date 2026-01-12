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
}

public class SpeedLimitService : ISpeedLimitService
{
    private SpeedLimitOptions _options;
    private readonly IMemoryCache _cache;
    private static readonly NLog.Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<string, PartitionedRateLimiter<HttpContext>> _allRateLimiter = [];

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
}
