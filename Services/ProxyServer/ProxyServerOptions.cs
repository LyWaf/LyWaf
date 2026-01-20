namespace LyWaf.Services.ProxyServer;

/// <summary>
/// 代理服务器配置选项
/// 支持 HTTP 代理、HTTPS 代理（CONNECT）和 SOCKS5 代理
/// </summary>
public class ProxyServerOptions
{
    /// <summary>
    /// 是否启用代理服务
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 代理服务监听的端口列表
    /// 可以为不同端口配置不同的代理类型
    /// 键为端口号字符串（如 "8080"）
    /// </summary>
    public Dictionary<string, ProxyPortConfig> Ports { get; set; } = [];

    /// <summary>
    /// 默认代理配置（适用于未单独配置的端口）
    /// </summary>
    public ProxyPortConfig Default { get; set; } = new();

    /// <summary>
    /// 允许代理访问的目标主机列表（白名单）
    /// 为空表示允许所有
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// 禁止代理访问的目标主机列表（黑名单）
    /// </summary>
    public List<string> BlockedHosts { get; set; } = [];

    /// <summary>
    /// 需要认证时的用户名
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// 需要认证时的密码
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 连接超时时间（秒）
    /// </summary>
    public int ConnectTimeout { get; set; } = 30;

    /// <summary>
    /// 数据传输超时时间（秒）
    /// </summary>
    public int DataTimeout { get; set; } = 300;
}

/// <summary>
/// 单个端口的代理配置
/// </summary>
public class ProxyPortConfig
{
    /// <summary>
    /// 是否启用 HTTP 代理（普通 HTTP 请求转发）
    /// </summary>
    public bool EnableHttp { get; set; } = true;

    /// <summary>
    /// 是否启用 HTTPS 代理（CONNECT 隧道）
    /// </summary>
    public bool EnableHttps { get; set; } = true;

    /// <summary>
    /// 是否启用 SOCKS5 代理
    /// </summary>
    public bool EnableSocks5 { get; set; } = false;

    /// <summary>
    /// 是否需要认证
    /// </summary>
    public bool RequireAuth { get; set; } = false;
}
