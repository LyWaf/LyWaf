namespace LyWaf.Services.StreamServer;

/// <summary>
/// TCP/UDP 流代理服务器的全局配置。
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

    /// <summary>监听和转发协议，默认 TCP。</summary>
    public StreamProtocol Protocol { get; set; } = StreamProtocol.Tcp;

    /// <summary>是否读取客户端发送的 PROXY protocol v1 头。</summary>
    public bool AcceptProxyProtocol { get; set; }

    /// <summary>允许提交 PROXY 头的直接对端 CIDR；接受模式下不能为空。</summary>
    public List<string> ProxyProtocolTrustedNetworks { get; set; } = [];

    /// <summary>是否向上游发送 PROXY protocol v1 头。</summary>
    public bool SendProxyProtocol { get; set; }

    /// <summary>是否预读 TLS ClientHello，并按 SNI 选择上游。</summary>
    public bool SniPreread { get; set; }

    /// <summary>SNI 主机名到上游列表的映射，支持以 * 开头的后缀模式。</summary>
    public Dictionary<string, List<string>> SniUpstreams { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>允许的客户端 CIDR；为空表示不启用白名单。</summary>
    public List<string> AllowedNetworks { get; set; } = [];

    /// <summary>拒绝的客户端 CIDR，优先级高于允许列表。</summary>
    public List<string> DeniedNetworks { get; set; } = [];

    /// <summary>该监听器允许的最大并发 TCP 连接，0 表示不限制。</summary>
    public int MaxConnections { get; set; }

    /// <summary>每个方向的速率上限（字节/秒），0 表示不限制。</summary>
    public long BytesPerSecond { get; set; }

    /// <summary>UDP 客户端会话无流量后保留的秒数。</summary>
    public int UdpSessionTimeout { get; set; } = 30;

    /// <summary>单个 UDP 监听器允许保留的最大客户端会话数。</summary>
    public int MaxUdpSessions { get; set; } = 4096;
}

/// <summary>Stream 监听协议。</summary>
public enum StreamProtocol
{
    /// <summary>面向连接的 TCP。</summary>
    Tcp,
    /// <summary>基于数据报的 UDP。</summary>
    Udp
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
    First,

    /// <summary>选择当前活动连接最少的健康上游。</summary>
    LeastConnections,

    /// <summary>按客户端 IP 稳定散列选择健康上游。</summary>
    ClientIpHash
}
