namespace LyWaf.Services.Dns;

/// <summary>
/// 自定义 DNS 配置选项
/// 允许将指定域名解析为指定的 IP 地址列表
/// </summary>
public class CustomDnsOptions
{
    /// <summary>
    /// 是否启用自定义 DNS
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// DNS 解析映射
    /// Key: 域名（支持通配符，如 *.example.com）
    /// Value: IP 地址列表，将随机选择其中一个
    /// </summary>
    public Dictionary<string, DnsEntry> Entries { get; set; } = [];

    /// <summary>
    /// 自定义 DNS 服务器列表（可选）
    /// 当自定义映射未命中时，依次向这些 DNS 服务器发起查询
    /// 格式: "IP" 或 "IP:Port"，如 "8.8.8.8"、"1.1.1.1:53"
    /// 留空则使用系统默认 DNS
    /// </summary>
    public List<string> DnsServers { get; set; } = [];

    /// <summary>
    /// DNS 查询超时（毫秒），默认 3000ms
    /// </summary>
    public int DnsTimeoutMs { get; set; } = 3000;

    /// <summary>
    /// DNS 解析缓存时间（秒），0 表示不缓存
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 300;
}

/// <summary>
/// 单个域名的 DNS 解析条目
/// </summary>
public class DnsEntry
{
    /// <summary>
    /// IP 地址列表
    /// </summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>
    /// 负载均衡策略: Random（随机）, RoundRobin（轮询）
    /// </summary>
    public string Policy { get; set; } = "Random";

    /// <summary>
    /// 单条目 TTL 覆盖（秒），-1 使用全局配置
    /// </summary>
    public int TtlSeconds { get; set; } = -1;
}
