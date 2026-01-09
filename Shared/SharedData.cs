using System.Collections.Concurrent;
using LyWaf.Services.SpeedLimit;
using LyWaf.Struct;

namespace LyWaf.Shared;

/// <summary>
/// 全局共享数据存储
/// 用于存储各类统计信息、限速状态、封禁记录等运行时数据
/// 所有数据使用 ExpiringSafeDictionary 实现自动过期清理
/// </summary>
public static class SharedData
{
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
    /// 客户端最后访问时间
    /// Key: 客户端IP地址
    /// Value: Unix时间戳（毫秒）
    /// 用途: 记录每个客户端的最后一次访问时间，用于活跃度分析
    /// 过期时间: 60分钟，清理间隔: 30分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, long> ClientTimes =
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
    /// 客户端带宽限速状态
    /// Key: 客户端IP地址
    /// Value: 限速状态（包含令牌桶的剩余令牌、上次填充时间等）
    /// 用途: 实现基于IP的带宽限速，使用令牌桶算法控制下载速度
    /// 过期时间: 10分钟，清理间隔: 20分钟
    /// </summary>
    public static readonly ExpiringSafeDictionary<string, ClientThrottledLimit> ClientThrottled =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(20));
}