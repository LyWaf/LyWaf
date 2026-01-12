using System.Net;
using System.Threading.RateLimiting;

namespace LyWaf.Services.SpeedLimit;

public class SpeedLimitOptions
{
    public Dictionary<string, SpeedLimitPolicyOptions> Limits { get; set; } = [];

    public ThrottledOptions Throttled { get; set; } = new();
    public int RejectCode { get; set; } = 429;

    public string? Default { get; set; } = null;
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
