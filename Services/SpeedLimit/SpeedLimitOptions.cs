using System.Net;
using System.Threading.RateLimiting;

namespace LyWaf.Services.SpeedLimit;

public class SpeedLimitOptions
{
    public Dictionary<string, SpeedLimitPolicyOptions> Limits { get; set; } = [];

    public ThrottledOptions Throttled { get; set; } = new();
    public int RejectCode { get; set; } = 429;

    public string? Default { get; set; } = null;

    /// <summary>
    /// IP访问控制配置
    /// </summary>
    public AccessControlOptions AccessControl { get; set; } = new();

    /// <summary>
    /// 连接限制配置
    /// </summary>
    public ConnectionLimitOptions ConnectionLimit { get; set; } = new();
}

public class SpeedLimitPolicyOptions
{
    public string Name { get; set; } = "fixed";
    public string? Partition { get; set; } = null;
    public int PermitLimit { get; set; } = 50;
    public int SegmentsPerWindow { get; set; } = 50;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    public int QueueLimit { get; set; } = 20;
    public QueueProcessingOrder QueueOrder { get; set; } = QueueProcessingOrder.OldestFirst;
    public TimeSpan? ReplenishmentPeriod { get; set; }
    public int TokensPerPeriod { get; set; } = 20;
}

public class ThrottledOptions
{
    public int Global { get; set; } = 0;
    public Dictionary<string, int> Everys { get; set; } = [];

    public Dictionary<string, int> IpEverys { get; set; } = [];
}

/// <summary>
/// IP访问控制配置
/// 支持IP地址和CIDR格式，如: 192.168.1.1, 192.168.1.0/24, 10.0.0.0/8
/// </summary>
public class AccessControlOptions
{
    /// <summary>
    /// 是否启用访问控制
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 访问控制模式: Whitelist(白名单模式，只允许列表中的IP) 或 Blacklist(黑名单模式，拒绝列表中的IP)
    /// </summary>
    public AccessControlMode Mode { get; set; } = AccessControlMode.Blacklist;

    /// <summary>
    /// IP白名单，支持CIDR格式
    /// 白名单模式下，只有在此列表中的IP才能访问
    /// 黑名单模式下，此列表中的IP不受黑名单限制
    /// </summary>
    public List<string> Whitelist { get; set; } = [];

    /// <summary>
    /// IP黑名单，支持CIDR格式
    /// 在黑名单模式下，此列表中的IP将被拒绝访问
    /// </summary>
    public List<string> Blacklist { get; set; } = [];

    /// <summary>
    /// 基于路径的访问控制规则
    /// Key为路径（支持通配符），Value为规则配置
    /// </summary>
    public Dictionary<string, PathAccessRule> PathRules { get; set; } = [];

    /// <summary>
    /// 拒绝访问时返回的HTTP状态码
    /// </summary>
    public int RejectStatusCode { get; set; } = 403;

    /// <summary>
    /// 拒绝访问时返回的消息，支持占位符: {ClientIp}, {Path}, {Method}, {Host}, {Time}
    /// </summary>
    public string RejectMessage { get; set; } = "Access Denied: {ClientIp}";
}

/// <summary>
/// 访问控制模式
/// </summary>
public enum AccessControlMode
{
    /// <summary>
    /// 白名单模式：只允许白名单中的IP访问
    /// </summary>
    Whitelist,

    /// <summary>
    /// 黑名单模式：拒绝黑名单中的IP访问
    /// </summary>
    Blacklist
}

/// <summary>
/// 基于路径的访问规则
/// </summary>
public class PathAccessRule
{
    /// <summary>
    /// 允许访问的IP列表，支持CIDR
    /// </summary>
    public List<string> Allow { get; set; } = [];

    /// <summary>
    /// 拒绝访问的IP列表，支持CIDR
    /// </summary>
    public List<string> Deny { get; set; } = [];
}

/// <summary>
/// 连接限制配置
/// </summary>
public class ConnectionLimitOptions
{
    /// <summary>
    /// 是否启用连接限制
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 每个客户端IP的最大并发连接数，0表示不限制
    /// </summary>
    public int MaxConnectionsPerIp { get; set; } = 0;

    /// <summary>
    /// 每个后端服务器的最大并发连接数，0表示不限制
    /// </summary>
    public int MaxConnectionsPerDestination { get; set; } = 0;

    /// <summary>
    /// 全局最大并发连接数，0表示不限制
    /// </summary>
    public int MaxTotalConnections { get; set; } = 0;

    /// <summary>
    /// 基于路径的连接限制
    /// Key为路径（支持通配符），Value为最大连接数
    /// </summary>
    public Dictionary<string, int> PathLimits { get; set; } = [];

    /// <summary>
    /// 超过连接限制时返回的HTTP状态码
    /// </summary>
    public int RejectStatusCode { get; set; } = 503;

    /// <summary>
    /// 超过连接限制时返回的消息，支持占位符: {ClientIp}, {Path}, {Method}, {Host}, {Time}
    /// </summary>
    public string RejectMessage { get; set; } = "Too Many Connections: {ClientIp}";
}

public class ClientThrottledLimit
{
    public TimeSpan Period = TimeSpan.FromSeconds(1);

    public int EveryCapacity = 1000000;

    public int LeftToken = 1000000;

    public DateTime LastRefillTime = DateTime.UtcNow;

    private const int MIN_STEP = 4;

    public int AllocToken(int token)
    {
        var now = DateTime.UtcNow;
        var timePassed = now - LastRefillTime;
        if(timePassed.TotalMilliseconds > Period.TotalMilliseconds / MIN_STEP) {
            var tokensToAdd = (int)((double)timePassed.TotalMilliseconds / Period.TotalMilliseconds * EveryCapacity);
            LastRefillTime = now;
            LeftToken = Math.Min(LeftToken + tokensToAdd, EveryCapacity);
        }

        var succ = Math.Min(LeftToken, token);
        LeftToken -= succ;
        return succ;
    }
}

/// <summary>
/// IP网络地址，用于CIDR匹配
/// </summary>
public class IpNetwork
{
    public IPAddress NetworkAddress { get; }
    public int PrefixLength { get; }
    private readonly byte[] _networkBytes;
    private readonly byte[] _maskBytes;

    public IpNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        NetworkAddress = IPAddress.Parse(parts[0].Trim());
        
        if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var prefix))
        {
            PrefixLength = prefix;
        }
        else
        {
            // 没有前缀长度，表示单个IP
            PrefixLength = NetworkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        }

        _networkBytes = NetworkAddress.GetAddressBytes();
        _maskBytes = CreateMask(_networkBytes.Length, PrefixLength);

        // 应用掩码到网络地址
        for (int i = 0; i < _networkBytes.Length; i++)
        {
            _networkBytes[i] = (byte)(_networkBytes[i] & _maskBytes[i]);
        }
    }

    public bool Contains(IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        
        // 地址族不同，不匹配
        if (addressBytes.Length != _networkBytes.Length)
            return false;

        for (int i = 0; i < addressBytes.Length; i++)
        {
            if ((addressBytes[i] & _maskBytes[i]) != _networkBytes[i])
                return false;
        }

        return true;
    }

    public bool Contains(string ipString)
    {
        if (IPAddress.TryParse(ipString, out var address))
        {
            return Contains(address);
        }
        return false;
    }

    private static byte[] CreateMask(int length, int prefixLength)
    {
        var mask = new byte[length];
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes && i < length; i++)
        {
            mask[i] = 0xFF;
        }

        if (fullBytes < length && remainingBits > 0)
        {
            mask[fullBytes] = (byte)(0xFF << (8 - remainingBits));
        }

        return mask;
    }

    /// <summary>
    /// 尝试解析CIDR字符串
    /// </summary>
    public static bool TryParse(string cidr, out IpNetwork? network)
    {
        try
        {
            network = new IpNetwork(cidr);
            return true;
        }
        catch
        {
            network = null;
            return false;
        }
    }
}
