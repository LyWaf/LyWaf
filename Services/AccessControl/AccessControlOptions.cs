namespace LyWaf.Services.AccessControl;

/// <summary>
/// 访问控制配置选项
/// 整合 IP 访问控制、地理位置访问控制和连接限制
/// </summary>
public class AccessControlOptions
{
    /// <summary>
    /// 拒绝访问时返回的 HTTP 状态码
    /// </summary>
    public int RejectStatusCode { get; set; } = 403;

    /// <summary>
    /// 拒绝访问时返回的消息
    /// 支持占位符: {ClientIp}, {Path}, {Method}, {Host}, {Time}, {Country}, {Region}, {City}
    /// </summary>
    public string RejectMessage { get; set; } = "Access Denied: {ClientIp}";

    /// <summary>
    /// 全局 IP 白名单，支持 CIDR 格式
    /// 白名单中的 IP 直接放行，不受任何访问控制限制（包括 IpControl、GeoControl）
    /// </summary>
    public List<string> Whitelist { get; set; } = [];

    /// <summary>
    /// IP 访问控制配置（黑名单）
    /// </summary>
    public IpAccessControlConfig IpControl { get; set; } = new();

    /// <summary>
    /// 地理位置访问控制配置
    /// </summary>
    public GeoAccessControlConfig GeoControl { get; set; } = new();

    /// <summary>
    /// 连接限制配置
    /// </summary>
    public ConnectionLimitConfig ConnectionLimit { get; set; } = new();
}

/// <summary>
/// 连接限制配置
/// </summary>
public class ConnectionLimitConfig
{
    /// <summary>
    /// 是否启用连接限制
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 每个客户端 IP 的最大并发连接数，0 表示不限制
    /// </summary>
    public int MaxConnectionsPerIp { get; set; } = 0;

    /// <summary>
    /// 每个后端服务器的最大并发连接数，0 表示不限制
    /// </summary>
    public int MaxConnectionsPerDestination { get; set; } = 0;

    /// <summary>
    /// 全局最大并发连接数，0 表示不限制
    /// </summary>
    public int MaxTotalConnections { get; set; } = 0;

    /// <summary>
    /// 基于路径的连接限制
    /// Key 为路径（支持通配符），Value 为最大连接数
    /// </summary>
    public Dictionary<string, int> PathLimits { get; set; } = [];

    /// <summary>
    /// 超过连接限制时返回的 HTTP 状态码
    /// </summary>
    public int RejectStatusCode { get; set; } = 503;

    /// <summary>
    /// 超过连接限制时返回的消息
    /// 支持占位符: {ClientIp}, {Path}, {Method}, {Host}, {Time}
    /// </summary>
    public string RejectMessage { get; set; } = "Too Many Connections: {ClientIp}";
}

/// <summary>
/// IP 访问控制配置（黑名单模式）
/// 支持 IP 地址和 CIDR 格式
/// 白名单在 AccessControlOptions.Whitelist 中配置
/// </summary>
public class IpAccessControlConfig
{
    /// <summary>
    /// 是否启用 IP 黑名单访问控制
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// IP 黑名单，支持 CIDR 格式
    /// 黑名单中的 IP 将被拒绝访问
    /// </summary>
    public List<string> Blacklist { get; set; } = [];

    /// <summary>
    /// 基于路径的 IP 访问规则
    /// </summary>
    public Dictionary<string, IpPathRule> PathRules { get; set; } = [];
}

/// <summary>
/// 基于路径的 IP 访问规则
/// </summary>
public class IpPathRule
{
    /// <summary>
    /// 白名单：允许访问的 IP 列表，支持 CIDR
    /// </summary>
    public List<string> Whitelist { get; set; } = [];

    /// <summary>
    /// 黑名单：拒绝访问的 IP 列表，支持 CIDR
    /// </summary>
    public List<string> Blacklist { get; set; } = [];
}

/// <summary>
/// 地理位置访问控制配置
/// 基于 IP2Region 实现
/// </summary>
public class GeoAccessControlConfig
{
    /// <summary>
    /// 是否启用地理位置访问控制
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// IP2Region 数据库文件路径
    /// </summary>
    public string DatabasePath { get; set; } = "ip2region.xdb";

    /// <summary>
    /// 访问控制模式
    /// </summary>
    public GeoAccessMode Mode { get; set; } = GeoAccessMode.Deny;

    /// <summary>
    /// 允许访问的国家/地区列表
    /// 支持: 国家名称、省份、城市
    /// </summary>
    public List<string> AllowCountries { get; set; } = [];

    /// <summary>
    /// 禁止访问的国家/地区列表
    /// </summary>
    public List<string> DenyCountries { get; set; } = [];

    /// <summary>
    /// 基于路径的地理位置规则
    /// </summary>
    public Dictionary<string, GeoPathRule> PathRules { get; set; } = [];

    /// <summary>
    /// 地理位置拒绝消息（覆盖全局配置）
    /// 支持占位符: {ClientIp}, {Country}, {Region}, {City}
    /// </summary>
    public string? RejectMessage { get; set; }
}

/// <summary>
/// 地理位置访问控制模式
/// </summary>
public enum GeoAccessMode
{
    /// <summary>
    /// 允许模式：只有列表中的国家可以访问
    /// </summary>
    Allow,

    /// <summary>
    /// 禁止模式：列表中的国家被禁止
    /// </summary>
    Deny
}

/// <summary>
/// 基于路径的地理位置规则
/// </summary>
public class GeoPathRule
{
    /// <summary>
    /// 白名单：允许访问的国家/地区列表
    /// </summary>
    public List<string> Whitelist { get; set; } = [];

    /// <summary>
    /// 黑名单：禁止访问的国家/地区列表
    /// </summary>
    public List<string> Blacklist { get; set; } = [];
}
