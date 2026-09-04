using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LyWaf.Services.StreamServer;

/// <summary>Stream 配置中已解析的主机与端口。</summary>
internal sealed record StreamEndpoint(string Host, int Port);

/// <summary>Stream 端点、CIDR 与负载选择的无 I/O 基础逻辑。</summary>
internal static class StreamPrimitives
{
    public static bool TryParseEndpoint(string value, out StreamEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string host;
        string portText;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']');
            if (close <= 1 || close + 2 >= value.Length || value[close + 1] != ':') return false;
            host = value[1..close];
            portText = value[(close + 2)..];
        }
        else
        {
            if (value.Count(character => character == ':') != 1) return false;
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1) return false;
            host = value[..colon];
            portText = value[(colon + 1)..];
        }
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host)) return false;
        endpoint = new StreamEndpoint(host, port);
        return true;
    }

    public static bool IsClientAllowed(IPAddress address, IReadOnlyList<string> allowed, IReadOnlyList<string> denied)
    {
        if (denied.Any(cidr => Contains(cidr, address))) return false;
        return allowed.Count == 0 || allowed.Any(cidr => Contains(cidr, address));
    }

    public static bool IsValidNetwork(string cidr)
    {
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var address)) return false;
        if (parts.Length == 1) return true;
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= max;
    }

    public static bool IsValidConfig(StreamConfig config) => config.Upstreams.Count > 0 &&
        config.Upstreams.Concat(config.SniUpstreams.Values.SelectMany(static value => value))
            .All(value => TryParseEndpoint(value, out _)) &&
        config.AllowedNetworks.Concat(config.DeniedNetworks).Concat(config.ProxyProtocolTrustedNetworks).All(IsValidNetwork) &&
        (!config.AcceptProxyProtocol || config.ProxyProtocolTrustedNetworks.Count > 0) &&
        (config.Protocol == StreamProtocol.Tcp || (!config.AcceptProxyProtocol && !config.SendProxyProtocol)) &&
        (!config.SniPreread || config.Protocol == StreamProtocol.Tcp) && config.MaxConnections >= 0 &&
        config.BytesPerSecond >= 0 && config.UdpSessionTimeout is >= 1 and <= 3600 &&
        config.MaxUdpSessions is >= 1 and <= 65536;

    public static IReadOnlyList<string> ResolveSniUpstreams(StreamConfig config, string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return config.Upstreams;
        if (config.SniUpstreams.TryGetValue(serverName, out var exact)) return exact;
        var wildcard = config.SniUpstreams
            .Where(pair => pair.Key.StartsWith("*.", StringComparison.Ordinal) &&
                serverName.EndsWith(pair.Key[1..], StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();
        return wildcard.Value ?? config.Upstreams;
    }

    private static bool Contains(string cidr, IPAddress address)
    {
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var network)) return false;
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length) return false;
        var bits = networkBytes.Length * 8;
        var prefix = bits;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bits)) return false;
        var full = prefix / 8;
        var partial = prefix % 8;
        for (var i = 0; i < full; i++) if (networkBytes[i] != addressBytes[i]) return false;
        if (partial == 0) return true;
        var mask = (byte)(0xff << (8 - partial));
        return (networkBytes[full] & mask) == (addressBytes[full] & mask);
    }
}

/// <summary>维护每个上游活动连接数并执行 Stream 负载选择。</summary>
internal sealed class StreamUpstreamSelector
{
    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private int _roundRobin;

    public string? Select(IReadOnlyList<string> upstreams, StreamLoadBalancePolicy policy, IPAddress? clientAddress)
    {
        if (upstreams.Count == 0) return null;
        return policy switch
        {
            StreamLoadBalancePolicy.First => upstreams[0],
            StreamLoadBalancePolicy.Random => upstreams[Random.Shared.Next(upstreams.Count)],
            StreamLoadBalancePolicy.LeastConnections => upstreams.OrderBy(item => _active.GetValueOrDefault(item)).ThenBy(item => item, StringComparer.Ordinal).First(),
            StreamLoadBalancePolicy.ClientIpHash when clientAddress is not null => upstreams[StableHash(clientAddress.GetAddressBytes()) % upstreams.Count],
            _ => upstreams[(int)((uint)(Interlocked.Increment(ref _roundRobin) - 1) % (uint)upstreams.Count)]
        };
    }

    public IDisposable Track(string upstream)
    {
        _active.AddOrUpdate(upstream, 1, static (_, value) => value + 1);
        return new Lease(this, upstream);
    }

    internal int Active(string upstream) => _active.GetValueOrDefault(upstream);
    private void Release(string upstream) => _active.AddOrUpdate(upstream, 0, static (_, value) => Math.Max(0, value - 1));
    private static int StableHash(byte[] bytes)
    {
        uint hash = 2166136261;
        foreach (var value in bytes) hash = (hash ^ value) * 16777619;
        return (int)(hash & 0x7fffffff);
    }

    private sealed class Lease(StreamUpstreamSelector owner, string upstream) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(upstream); }
    }
}

/// <summary>PROXY protocol v1 行的解析与生成工具。</summary>
internal static class ProxyProtocolV1
{
    public const int MaxHeaderBytes = 108;

    public static string Create(IPEndPoint source, IPEndPoint destination)
    {
        var family = source.AddressFamily == AddressFamily.InterNetwork ? "TCP4" : "TCP6";
        return $"PROXY {family} {source.Address} {destination.Address} {source.Port} {destination.Port}\r\n";
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out IPEndPoint? source, out IPEndPoint? destination, out int consumed)
    {
        source = destination = null;
        consumed = 0;
        if (bytes.Length > MaxHeaderBytes || bytes.Length < 8) return false;
        var end = bytes.IndexOf("\r\n"u8);
        if (end < 0) return false;
        consumed = end + 2;
        var parts = Encoding.ASCII.GetString(bytes[..end]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] == "PROXY" && parts[1] == "UNKNOWN") return true;
        if (parts.Length != 6 || parts[0] != "PROXY" || parts[1] is not ("TCP4" or "TCP6") ||
            !IPAddress.TryParse(parts[2], out var sourceIp) || !IPAddress.TryParse(parts[3], out var destinationIp) ||
            !int.TryParse(parts[4], out var sourcePort) || !int.TryParse(parts[5], out var destinationPort) ||
            sourcePort is < 1 or > 65535 || destinationPort is < 1 or > 65535) return false;
        if ((parts[1] == "TCP4") != (sourceIp.AddressFamily == AddressFamily.InterNetwork && destinationIp.AddressFamily == AddressFamily.InterNetwork)) return false;
        source = new IPEndPoint(sourceIp, sourcePort);
        destination = new IPEndPoint(destinationIp, destinationPort);
        return true;
    }
}
