namespace LyWaf.Services.StreamServer;

/// <summary>
/// TCP 流代理服务器配置选项
/// 用于 TCP 端口映射转发
/// </summary>
public class StreamServerOptions
{
    /// <summary>
    /// 是否启用流代理服务
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 流代理配置列表
    /// 键为监听地址（如 "8080", "0.0.0.0:3306"）
    /// </summary>
    public Dictionary<string, StreamConfig> Streams { get; set; } = [];

    /// <summary>
    /// 默认连接超时时间（秒）
    /// </summary>
    public int ConnectTimeout { get; set; } = 30;

    /// <summary>
    /// 默认数据传输超时时间（秒）
    /// </summary>
    public int DataTimeout { get; set; } = 300;

    /// <summary>
    /// 健康检查间隔时间（秒）
    /// </summary>
    public int HealthCheckInterval { get; set; } = 10;

    /// <summary>
    /// 健康检查连接超时时间（秒）
    /// </summary>
    public int HealthCheckTimeout { get; set; } = 3;

    /// <summary>
    /// 连续失败多少次后标记为不健康
    /// </summary>
    public int UnhealthyThreshold { get; set; } = 2;

    /// <summary>
    /// 连续成功多少次后标记为健康
    /// </summary>
    public int HealthyThreshold { get; set; } = 1;
}

/// <summary>
/// 单个流代理配置
/// </summary>
public class StreamConfig
{
    /// <summary>
    /// 上游服务器列表
    /// 支持格式: "ip:port" 或 "hostname:port"
    /// </summary>
    public List<string> Upstreams { get; set; } = [];

    /// <summary>
    /// 负载均衡策略
    /// </summary>
    public StreamLoadBalancePolicy Policy { get; set; } = StreamLoadBalancePolicy.RoundRobin;

    /// <summary>
    /// 连接超时时间（秒），覆盖默认值
    /// </summary>
    public int? ConnectTimeout { get; set; }

    /// <summary>
    /// 数据传输超时时间（秒），覆盖默认值
    /// </summary>
    public int? DataTimeout { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 负载均衡策略
/// </summary>
public enum StreamLoadBalancePolicy
{
    /// <summary>
    /// 轮询
    /// </summary>
    RoundRobin,

    /// <summary>
    /// 随机
    /// </summary>
    Random,

    /// <summary>
    /// 第一个可用
    /// </summary>
    First
}
