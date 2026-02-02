using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.ABTest;

/// <summary>
/// A/B 测试配置选项
/// </summary>
public class ABTestOptions
{
    /// <summary>
    /// 是否启用 A/B 测试
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A/B 测试配置列表
    /// Key 为测试 ID
    /// </summary>
    public Dictionary<string, ABTestConfig> Tests { get; set; } = new();
}

/// <summary>
/// A/B 测试服务接口
/// </summary>
public interface IABTestService
{
    /// <summary>
    /// 获取 A/B 测试配置
    /// </summary>
    ABTestConfig? GetConfig(string testId);

    /// <summary>
    /// 获取所有 A/B 测试配置
    /// </summary>
    Dictionary<string, ABTestConfig> GetAllConfigs();

    /// <summary>
    /// 添加或更新 A/B 测试配置
    /// </summary>
    void SetConfig(string testId, ABTestConfig config);

    /// <summary>
    /// 移除 A/B 测试配置
    /// </summary>
    bool RemoveConfig(string testId);

    /// <summary>
    /// 根据配置选择变体
    /// </summary>
    /// <param name="testId">测试 ID</param>
    /// <param name="context">HTTP 上下文</param>
    /// <returns>选中的变体名称</returns>
    string? SelectVariant(string testId, HttpContext context);

    /// <summary>
    /// 获取测试统计信息
    /// </summary>
    ABTestStats? GetStats(string testId);

    /// <summary>
    /// 重置测试统计
    /// </summary>
    void ResetStats(string testId);
}

/// <summary>
/// A/B 测试配置
/// </summary>
public class ABTestConfig
{
    /// <summary>
    /// 测试名称/描述
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 分配模式
    /// </summary>
    public ABTestMode Mode { get; set; } = ABTestMode.Random;

    /// <summary>
    /// Cookie 名称（用于会话保持）
    /// </summary>
    public string CookieName { get; set; } = "ab_variant";

    /// <summary>
    /// Cookie 有效期（天）
    /// </summary>
    public int CookieExpireDays { get; set; } = 30;

    /// <summary>
    /// 变体列表，Key 为变体名称，Value 为权重（百分比）
    /// 例如: { "A": 70, "B": 30 } 表示 70% 流量到 A，30% 到 B
    /// </summary>
    public Dictionary<string, int> Variants { get; set; } = new();

    /// <summary>
    /// 变体对应的目标（Cluster ID 或 Destination ID）
    /// 例如: { "A": "cluster-a", "B": "cluster-b" }
    /// </summary>
    public Dictionary<string, string> VariantTargets { get; set; } = new();

    /// <summary>
    /// 匹配路径（支持通配符 *）
    /// </summary>
    public List<string> MatchPaths { get; set; } = new();

    /// <summary>
    /// 排除路径（支持通配符 *）
    /// </summary>
    public List<string> ExcludePaths { get; set; } = new();
}

/// <summary>
/// A/B 测试分配模式
/// </summary>
public enum ABTestMode
{
    /// <summary>
    /// 纯随机：每次请求都随机分配
    /// </summary>
    Random,

    /// <summary>
    /// Cookie 会话保持：首次随机分配后，通过 Cookie 保持一致
    /// </summary>
    CookieSticky,

    /// <summary>
    /// IP 哈希：基于客户端 IP 分配，同一 IP 始终访问同一变体
    /// </summary>
    IpHash,

    /// <summary>
    /// 用户 ID 哈希：基于指定的用户标识分配
    /// </summary>
    UserIdHash
}

/// <summary>
/// A/B 测试统计信息
/// </summary>
public class ABTestStats
{
    public string TestId { get; set; } = "";
    public long TotalRequests { get; set; }
    public Dictionary<string, long> VariantHits { get; set; } = new();
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime LastRequestTime { get; set; }
}

/// <summary>
/// A/B 测试服务实现
/// </summary>
public class ABTestService : IABTestService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    
    // A/B 测试配置存储
    private readonly ConcurrentDictionary<string, ABTestConfig> _configs = new();
    
    // 统计信息存储
    private readonly ConcurrentDictionary<string, ABTestStats> _stats = new();
    
    private readonly object _statsLock = new();

    public ABTestService(IOptions<ABTestOptions> options)
    {
        var opts = options.Value;
        if (opts.Enabled && opts.Tests.Count > 0)
        {
            foreach (var kv in opts.Tests)
            {
                SetConfig(kv.Key, kv.Value);
            }
            _logger.Info("从配置文件加载了 {Count} 个 A/B 测试配置", opts.Tests.Count);
        }
    }

    public ABTestConfig? GetConfig(string testId)
    {
        return _configs.TryGetValue(testId, out var config) ? config : null;
    }

    public Dictionary<string, ABTestConfig> GetAllConfigs()
    {
        return new Dictionary<string, ABTestConfig>(_configs);
    }

    public void SetConfig(string testId, ABTestConfig config)
    {
        _configs[testId] = config;
        
        // 初始化统计信息
        if (!_stats.ContainsKey(testId))
        {
            _stats[testId] = new ABTestStats
            {
                TestId = testId,
                VariantHits = config.Variants.Keys.ToDictionary(k => k, _ => 0L)
            };
        }
        
        _logger.Info("已设置 A/B 测试配置: TestId={TestId}, Name={Name}, Mode={Mode}, Variants={Variants}",
            testId, config.Name, config.Mode, string.Join(",", config.Variants.Select(v => $"{v.Key}:{v.Value}%")));
    }

    public bool RemoveConfig(string testId)
    {
        var removed = _configs.TryRemove(testId, out _);
        if (removed)
        {
            _stats.TryRemove(testId, out _);
            _logger.Info("已移除 A/B 测试配置: TestId={TestId}", testId);
        }
        return removed;
    }

    public string? SelectVariant(string testId, HttpContext context)
    {
        if (!_configs.TryGetValue(testId, out var config) || !config.Enabled)
            return null;

        if (config.Variants.Count == 0)
            return null;

        // 检查路径匹配
        var path = context.Request.Path.Value ?? "/";
        if (!IsPathMatched(path, config))
            return null;

        string selectedVariant;

        switch (config.Mode)
        {
            case ABTestMode.CookieSticky:
                selectedVariant = SelectWithCookieSticky(context, config);
                break;

            case ABTestMode.IpHash:
                selectedVariant = SelectWithIpHash(context, config);
                break;

            case ABTestMode.UserIdHash:
                selectedVariant = SelectWithUserIdHash(context, config);
                break;

            case ABTestMode.Random:
            default:
                selectedVariant = SelectRandom(config);
                break;
        }

        // 更新统计
        UpdateStats(testId, selectedVariant);

        return selectedVariant;
    }

    private bool IsPathMatched(string path, ABTestConfig config)
    {
        // 检查排除路径
        if (config.ExcludePaths.Count > 0)
        {
            foreach (var excludePath in config.ExcludePaths)
            {
                if (MatchPath(path, excludePath))
                    return false;
            }
        }

        // 如果没有指定匹配路径，则匹配所有
        if (config.MatchPaths.Count == 0)
            return true;

        // 检查匹配路径
        foreach (var matchPath in config.MatchPaths)
        {
            if (MatchPath(path, matchPath))
                return true;
        }

        return false;
    }

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

    private string SelectWithCookieSticky(HttpContext context, ABTestConfig config)
    {
        var cookieName = $"{config.CookieName}";
        
        // 尝试从 Cookie 获取已分配的变体
        if (context.Request.Cookies.TryGetValue(cookieName, out var existingVariant))
        {
            // 验证变体是否仍然有效
            if (config.Variants.ContainsKey(existingVariant))
            {
                return existingVariant;
            }
        }

        // 随机分配新变体
        var selectedVariant = SelectRandom(config);

        // 设置 Cookie
        context.Response.Cookies.Append(cookieName, selectedVariant, new CookieOptions
        {
            Expires = DateTimeOffset.Now.AddDays(config.CookieExpireDays),
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });

        return selectedVariant;
    }

    private string SelectWithIpHash(HttpContext context, ABTestConfig config)
    {
        var clientIp = GetClientIp(context);
        return SelectByHash(clientIp, config);
    }

    private string SelectWithUserIdHash(HttpContext context, ABTestConfig config)
    {
        // 尝试从多个来源获取用户标识
        var userId = context.Request.Headers["X-User-Id"].FirstOrDefault()
            ?? context.Request.Query["user_id"].FirstOrDefault()
            ?? context.Request.Cookies["user_id"]
            ?? GetClientIp(context);

        return SelectByHash(userId, config);
    }

    private static string SelectByHash(string key, ABTestConfig config)
    {
        if (string.IsNullOrEmpty(key))
            return SelectRandom(config);

        var hash = GetStableHash(key);
        var totalWeight = config.Variants.Values.Sum();
        var target = (int)(hash % (uint)totalWeight);

        var cumulative = 0;
        foreach (var variant in config.Variants)
        {
            cumulative += variant.Value;
            if (target < cumulative)
            {
                return variant.Key;
            }
        }

        return config.Variants.Keys.First();
    }

    private static string SelectRandom(ABTestConfig config)
    {
        var totalWeight = config.Variants.Values.Sum();
        var random = Random.Shared.Next(totalWeight);

        var cumulative = 0;
        foreach (var variant in config.Variants)
        {
            cumulative += variant.Value;
            if (random < cumulative)
            {
                return variant.Key;
            }
        }

        return config.Variants.Keys.First();
    }

    private static string GetClientIp(HttpContext context)
    {
        // 尝试从 X-Forwarded-For 获取
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff))
        {
            var ip = xff.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(ip))
                return ip;
        }

        // 尝试从 X-Real-IP 获取
        var xri = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xri))
            return xri;

        // 使用连接 IP
        return context.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private static uint GetStableHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }

    private void UpdateStats(string testId, string variant)
    {
        lock (_statsLock)
        {
            if (_stats.TryGetValue(testId, out var stats))
            {
                stats.TotalRequests++;
                stats.LastRequestTime = DateTime.Now;
                
                if (stats.VariantHits.ContainsKey(variant))
                    stats.VariantHits[variant]++;
                else
                    stats.VariantHits[variant] = 1;
            }
        }
    }

    public ABTestStats? GetStats(string testId)
    {
        return _stats.TryGetValue(testId, out var stats) ? stats : null;
    }

    public void ResetStats(string testId)
    {
        if (_stats.TryGetValue(testId, out var stats) && _configs.TryGetValue(testId, out var config))
        {
            lock (_statsLock)
            {
                stats.TotalRequests = 0;
                stats.VariantHits = config.Variants.Keys.ToDictionary(k => k, _ => 0L);
                stats.StartTime = DateTime.Now;
                stats.LastRequestTime = DateTime.MinValue;
            }
            _logger.Info("已重置 A/B 测试统计: TestId={TestId}", testId);
        }
    }
}
