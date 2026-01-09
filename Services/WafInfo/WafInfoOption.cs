

using System.Net;
using System.Net.Http.Headers;
using NLog;

namespace LyWaf.Services.WafInfo;

public class WafInfoOptions
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    public List<OneLinstenInfo> Listens { get; set; } = [];
    public OneLinstenInfo? ControlListen { get; set; } = null;
    public List<OneCertInfo> Certs { get; set; } = [];
    public Dictionary<string, string> HeaderUps { get; set; } = [];
    public Dictionary<string, string> HeaderDowns { get; set; } = [];
    public string? ChangeHost { get; set; } = null;
    public ForwardedInfo? Forwarded { get; set; } = null; // set, append, none

    private static readonly HashSet<string> LoopbackAddresses =
    [
        "127.0.0.1",
        "::1",
        "localhost"
    ];

    private static readonly OneLinstenInfo DefaultControlListen = new()
    {
        Host = "127.0.0.1",
        Port = 7030
    };

    /// <summary>
    /// 获取 ControlListen，如果为空则返回默认配置
    /// 同时检查非回环地址并输出警告
    /// </summary>
    public OneLinstenInfo GetControlListen()
    {
        var listen = ControlListen ?? DefaultControlListen;

        // 检查 ControlListen 的 Host 是否为非回环地址
        if (!IsLoopbackAddress(listen.Host))
        {
            _logger.Warn("⚠ 警告: ControlListen 的 Host '{Host}:{Port}' 不是本地回环地址，可能存在安全风险！", listen.Host, listen.Port);
        }

        return listen;
    }

    private static bool IsLoopbackAddress(string host)
    {
        if (LoopbackAddresses.Contains(host.ToLower()))
        {
            return true;
        }

        // 检查是否是 127.x.x.x 格式的回环地址
        if (IPAddress.TryParse(host, out var ip))
        {
            return IPAddress.IsLoopback(ip);
        }

        return false;
    }
}


public class ForwardedInfo
{
    public string Method { get; set; } = "append"; //set, append, none
    // true: X-Forwarded-* 格式, false: Forwarded (RFC 7239) 格式
    public bool IsX { get; set; } = false;
    public string? For { get; set; } = null;
    public string? Proto { get; set; } = null;
    public string? Host { get; set; } = null;
}

public class OneLinstenInfo
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 80;
    public bool IsHttps { get; set; } = false;
    /// <summary>
    /// 自动 HTTPS 重定向端口，如果为有效端口则自动将 HTTP 请求重定向到 HTTPS
    /// </summary>
    public int? AutoHttpsPort { get; set; } = null;
}

public class OneCertInfo
{
    public string Host { get; set; } = "*";
    public string PemFile { get; set; } = "";
    public string? KeyFile { get; set; } = null;
}