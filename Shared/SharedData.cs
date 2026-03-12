using System.Collections.Concurrent;
using System.Collections.Generic;
using LyWaf.Services.Captcha;
using LyWaf.Services.SpeedLimit;
using LyWaf.Struct;

namespace LyWaf.Shared;

/// <summary>
/// 拦截事件（按 IP+应用 聚合）
/// </summary>
public class InterceptEvent
{
    public string SourceIp { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public string Application { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string RuleType { get; set; } = "";
    public long HitCount;
    public DateTime FirstHitTime { get; set; } = DateTime.UtcNow;
    public DateTime LastHitTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 全局共享数据存储
/// 用于存储各类统计信息、限速状态、封禁记录等运行时数据
/// 所有数据使用 ExpiringSafeDictionary 实现自动过期清理
/// </summary>
public static class SharedData
{
    /// <summary>
    /// 当前使用的配置文件路径（启动时设置）
    /// </summary>
    public static string ConfigFilePath { get; set; } = "config.ly";

    /// <summary>
    /// 是否存在有效的草稿配置文件
    /// 由配置加载器在 Load() 时自动检测并设置
    /// 用于 API 返回给前端判断当前运行的是否为草稿配置
    /// </summary>
    public static bool ExistsDraftConfig { get; set; } = false;

    /// <summary>
    /// 是否请求重启 WebApplication（端口变更时设为 true）
    /// DoStartRun 中的重启循环会检测此标记，重建 WebApplication 以应用新端口
    /// </summary>
    public static volatile bool RestartRequested = false;

    /// <summary>
    /// 当前是否为首台服务器（直接面对客户端，不经过代理）
    /// 为 true 时 GetClientIp 直接取连接 IP，忽略代理头
    /// </summary>
    public static volatile bool IsFirstServer = false;

    /// <summary>
    /// IP 通用数据字典
    /// Key: IP地址
    /// Value: 任意对象数据
    /// 用途: 存储与IP相关的临时数据
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, object> IpDict = new();

    /// <summary>
    /// 限制数据字典
    /// Key: 限制标识
    /// Value: 限制相关数据
    /// 用途: 存储各类限制的临时状态
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, object> Limit = new();

    /// <summary>
    /// 通用缓存字典
    /// Key: 缓存键
    /// Value: 缓存数据
    /// 用途: 通用数据缓存
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, object> CacheDict = new();

    /// <summary>
    /// 目标服务器统计
    /// Key: 目标服务器地址/标识
    /// Value: 统计信息（请求次数、响应时间等）
    /// 用途: 统计每个后端服务器的访问情况
    /// 过期时间: 60分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, IpStatistic> DestStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    /// <summary>
    /// 请求路径统计
    /// Key: 请求路径（如 /api/users）
    /// Value: 统计信息（请求次数、响应时间等）
    /// 用途: 统计每个API路径的访问情况，用于性能分析
    /// 过期时间: 60分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, IpStatistic> ReqStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    /// <summary>
    /// 客户端IP统计
    /// Key: 客户端IP地址
    /// Value: 统计信息（请求次数、响应时间、访问的URL分布等）
    /// 用途: 统计每个客户端IP的访问行为，用于安全分析和CC检测
    /// 过期时间: 60分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, IpStatistic> ClientStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    /// <summary>
    /// 新客户端访问次数
    /// Key: 客户端IP地址
    /// Value: 访问次数
    /// 用途: 统计新客户端的访问频率，用于识别异常访问模式
    /// 过期时间: 60分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, long> NewClientVisits =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    /// <summary>
    /// CC攻击限制统计
    /// Key: 限制键（通常为 IP+路径 的组合）
    /// Value: 在限制周期内的请求次数
    /// 用途: 用于CC攻击检测，当请求次数超过阈值时触发封禁
    /// 过期时间: 10分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, long> LimitCcStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    /// <summary>
    /// 客户端详细访问记录
    /// Key: 客户端IP地址
    /// Value: 最近的请求记录列表（包含路径、耗时、状态码等）
    /// 用途: 保存客户端最近的访问详情，用于异常行为分析
    /// 过期时间: 10分钟，清理间隔: 10分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, LinkedList<ReqestShortMsg>> ClientDetailVisits =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(10));

    /// <summary>
    /// 被封禁的客户端IP
    /// Key: 客户端IP地址
    /// Value: 封禁原因（如 "CC攻击"、"异常访问" 等）
    /// 用途: 记录被封禁的IP及封禁原因，请求时会检查此列表拒绝访问
    /// 过期时间: 10分钟（默认，可通过封禁时指定），清理间隔: 10分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, string> ClientFb =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(10));

    /// <summary>
    /// 等待验证码验证的客户端IP
    /// Key: 客户端IP地址
    /// Value: CaptchaPendingInfo（包含规则名称、过期时间等）
    /// 用途: 当 CC 规则触发 Captcha 动作时，将 IP 加入此列表，
    ///       后续请求会被拦截并展示验证码页面，验证通过后移除
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, CaptchaPendingInfo> CaptchaPending =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(5));

    /// <summary>
    /// 客户端带宽限速状态
    /// Key: 客户端IP地址
    /// Value: 限速状态（包含令牌桶的剩余令牌、上次填充时间等）
    /// 用途: 实现基于IP的带宽限速，使用令牌桶算法控制下载速度
    /// 过期时间: 10分钟，清理间隔: 20分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, ClientThrottledLimit> ClientThrottled =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(20));

    /// <summary>
    /// API 耗时详细统计
    /// Key: "METHOD:PATH" (如 "GET:/api/users")
    /// Value: API 耗时统计信息
    /// 用途: 记录每个 API 的详细耗时信息，用于性能分析
    /// 过期时间: 60分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, ApiTimingStatistic> ApiTimings =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    /// <summary>
    /// 全局流量统计
    /// 用途: 记录总请求数、独立IP、拦截次数、错误统计等
    /// </summary>
    public static readonly TrafficStatistic Traffic = new();

    /// <summary>
    /// 安全态势统计
    /// 用途: 记录各类安全事件的历史数据、攻击源统计、趋势分析
    /// </summary>
    public static readonly SecurityStatistic Security = new();

    /// <summary>
    /// 地理位置流量统计
    /// 用途: 记录按国家和省份维度的访问量和拦截量，用于地图可视化
    /// </summary>
    public static readonly GeoTrafficStatistic GeoTraffic = new();

    /// <summary>
    /// QPS 追踪器
    /// 用途: 记录每秒请求数，支持按5秒/1分钟/1小时粒度查询
    /// </summary>
    public static readonly QpsTracker Qps = new();

    /// <summary>
    /// 带宽追踪器
    /// 用途: 记录每秒入站/出站字节数，支持按5秒/1分钟/1小时粒度查询
    /// </summary>
    public static readonly BandwidthTracker Bandwidth = new();

    /// <summary>
    /// IP 请求日志监控目标
    /// Key: 客户端IP地址
    /// Value: 添加时间
    /// 用途: 标记需要完整记录请求内容的 IP，所有来自这些 IP 的请求
    ///       将被完整记录到文件中（含请求头、请求体，每请求最多200KB）
    /// 支持自动过期清理
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, DateTime> IpLogTargets =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(5));

    // ============ 拦截事件统计 ============

    /// <summary>
    /// 拦截事件（按 IP+应用 聚合）
    /// Key: "ip|application"
    /// </summary>
    private static readonly ConcurrentDictionary<string, InterceptEvent> _interceptEvents = new();

    /// <summary>
    /// 记录一次拦截事件
    /// </summary>
    public static void RecordInterceptEvent(string sourceIp, string application, string region, string city, string ruleName, string ruleType)
    {
        var key = $"{sourceIp}|{application}";
        _interceptEvents.AddOrUpdate(key,
            _ => new InterceptEvent
            {
                SourceIp = sourceIp,
                Region = region,
                City = city,
                Application = application,
                RuleName = ruleName,
                RuleType = ruleType,
                HitCount = 1,
                FirstHitTime = DateTime.UtcNow,
                LastHitTime = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.HitCount);
                existing.LastHitTime = DateTime.UtcNow;
                existing.RuleName = ruleName;
                return existing;
            });
    }

    /// <summary>
    /// 获取拦截事件列表（支持过滤）
    /// </summary>
    public static List<InterceptEvent> GetInterceptEvents(string? ipFilter = null, string? domainFilter = null,
        DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = _interceptEvents.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(ipFilter))
            query = query.Where(e => e.SourceIp.Contains(ipFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(domainFilter))
            query = query.Where(e => e.Application.Contains(domainFilter, StringComparison.OrdinalIgnoreCase));
        if (startTime.HasValue)
            query = query.Where(e => e.FirstHitTime >= startTime.Value);
        if (endTime.HasValue)
            query = query.Where(e => e.LastHitTime <= endTime.Value);

        return query.OrderByDescending(e => e.LastHitTime).ToList();
    }

    /// <summary>
    /// 清除所有拦截事件
    /// </summary>
    public static void ClearInterceptEvents() => _interceptEvents.Clear();

    /// <summary>
    /// 释放所有静态字典资源（停止内部 Timer），用于应用关闭时的清理
    /// </summary>
    public static void DisposeAll()
    {
        IpDict.Dispose();
        Limit.Dispose();
        CacheDict.Dispose();
        DestStas.Dispose();
        ReqStas.Dispose();
        ClientStas.Dispose();
        NewClientVisits.Dispose();
        LimitCcStas.Dispose();
        ClientDetailVisits.Dispose();
        ClientFb.Dispose();
        CaptchaPending.Dispose();
        ClientThrottled.Dispose();
        ApiTimings.Dispose();
        IpLogTargets.Dispose();
    }
}
