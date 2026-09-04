using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LyWaf.Services.Discovery;

/// <summary>查询 A/AAAA/SRV 记录及 TTL，并将每个地址发布为独立 YARP Destination。</summary>
public sealed class DnsDiscoveryProvider : IServiceDiscoveryProvider
{
    /// <summary>配置中用于选择该实现的 Provider 类型名称。</summary>
    public string Type => "Dns";

    /// <summary>
    /// 查询配置主机的 A、AAAA 或 SRV 记录，并生成可直接交给 YARP 的完整实例集合。
    /// DNS 响应 TTL 会写入实例元数据，供协调器决定下一次刷新时间。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置缺失、端口无效或没有可用 DNS 服务器。</exception>
    /// <exception cref="InvalidDataException">DNS 服务返回格式错误、失败状态或被截断的数据包。</exception>
    /// <exception cref="TimeoutException">DNS 服务未在查询超时内响应。</exception>
    public async Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(
        string providerId, DiscoveryProviderOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Host)) throw new InvalidOperationException($"发现 Provider {providerId} 未配置 Host");
        if (options.RecordType.Equals("SRV", StringComparison.OrdinalIgnoreCase))
            return await DiscoverSrvAsync(providerId, options, cancellationToken);
        if (options.Port is < 1 or > 65535) throw new InvalidOperationException($"发现 Provider {providerId} 的 Port 无效");
        IReadOnlyList<AddressRecord> records;
        if (options.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(options.Host, out _))
            records = (await System.Net.Dns.GetHostAddressesAsync(options.Host, cancellationToken))
                .Select(static address => new AddressRecord(address, null)).ToArray();
        else
            records = await DiscoverAddressesAsync(options, cancellationToken);
        return records
            .Where(record => (record.Address.AddressFamily == AddressFamily.InterNetwork && options.IncludeIPv4) ||
                         (record.Address.AddressFamily == AddressFamily.InterNetworkV6 && options.IncludeIPv6))
            .GroupBy(static record => record.Address).Select(static group => group.OrderBy(item => item.Ttl).First())
            .OrderBy(record => record.Address.ToString(), StringComparer.Ordinal)
            .Select(record =>
            {
                var ip = record.Address;
                var host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
                var address = FileDiscoveryProvider.ValidateAddress($"{options.Scheme}://{host}:{options.Port}/");
                var metadata = new Dictionary<string, string> { ["dns.host"] = options.Host };
                if (record.Ttl is { } ttl) metadata["dns.ttl_seconds"] = ttl.ToString();
                return new DiscoveredDestination(
                    $"dns-{Convert.ToHexString(ip.GetAddressBytes()).ToLowerInvariant()}", address, metadata);
            }).ToArray();
    }

    private static async Task<IReadOnlyList<AddressRecord>> DiscoverAddressesAsync(DiscoveryProviderOptions options,
        CancellationToken token)
    {
        var server = GetDnsServer(options.DnsServer);
        var records = new List<AddressRecord>();
        if (options.IncludeIPv4) records.AddRange(await QueryAddressesAsync(server, options.Host, 1, token));
        if (options.IncludeIPv6) records.AddRange(await QueryAddressesAsync(server, options.Host, 28, token));
        return records;
    }

    private static async Task<IReadOnlyList<AddressRecord>> QueryAddressesAsync(IPAddress server, string host,
        ushort type, CancellationToken token)
    {
        var transactionId = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
        var response = await QueryAsync(server, BuildQuery(transactionId, host, type), token);
        return ParseAddressResponse(response, transactionId, type);
    }

    private static async Task<IReadOnlyList<DiscoveredDestination>> DiscoverSrvAsync(
        string providerId, DiscoveryProviderOptions options, CancellationToken cancellationToken)
    {
        var dnsServer = GetDnsServer(options.DnsServer);
        var transactionId = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
        var records = ParseSrvResponse(await QueryAsync(dnsServer, BuildQuery(transactionId, options.Host, 33), cancellationToken), transactionId);
        return records.Select((record, index) =>
        {
            var target = record.Target.TrimEnd('.');
            var address = FileDiscoveryProvider.ValidateAddress($"{options.Scheme}://{target}:{record.Port}/");
            return new DiscoveredDestination($"srv-{SanitizeId(target)}-{record.Port}-{index}", address,
                new Dictionary<string, string>
                {
                    ["dns.srv"] = options.Host,
                    ["dns.priority"] = record.Priority.ToString(),
                    ["dns.weight"] = record.Weight.ToString(),
                    ["dns.ttl_seconds"] = record.Ttl.ToString()
                });
        }).ToArray();
    }

    private static IPAddress GetDnsServer(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && IPAddress.TryParse(configured, out var parsed)) return parsed;
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .SelectMany(x => x.GetIPProperties().DnsAddresses)
            .FirstOrDefault(x => !x.Equals(IPAddress.Any) && !x.Equals(IPAddress.IPv6Any))
            ?? throw new InvalidOperationException("未找到可用 DNS 服务器，请配置 DnsServer");
    }

    private static async Task<byte[]> QueryAsync(IPAddress server, byte[] query, CancellationToken token)
    {
        using var client = new UdpClient(server.AddressFamily);
        await client.SendAsync(query, new IPEndPoint(server, 53), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { return (await client.ReceiveAsync(timeout.Token)).Buffer; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("DNS 查询超时"); }
    }

    private static byte[] BuildQuery(ushort transactionId, string name, ushort type)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new InvalidOperationException($"无效 DNS 名称: {name}");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        stream.Write(tail);
        return stream.ToArray();
    }

    internal static IReadOnlyList<(ushort Priority, ushort Weight, ushort Port, string Target)> ParseSrvPacket(byte[] data, ushort expectedId) =>
        ParseSrvResponse(data, expectedId).Select(x => (x.Priority, x.Weight, x.Port, x.Target)).ToArray();

    private static IReadOnlyList<SrvRecord> ParseSrvResponse(byte[] data, ushort expectedId)
    {
        if (data.Length < 12 || ReadUInt16(data, 0) != expectedId) throw new InvalidDataException("DNS SRV 响应无效");
        var flags = ReadUInt16(data, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0) throw new InvalidDataException($"DNS SRV 查询失败，rcode={flags & 0x000F}");
        var questions = ReadUInt16(data, 4);
        var answers = ReadUInt16(data, 6);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(data, ref offset);
            Ensure(data, offset, 4);
            offset += 4;
        }
        var result = new List<SrvRecord>();
        for (var i = 0; i < answers; i++)
        {
            ReadName(data, ref offset);
            Ensure(data, offset, 10);
            var type = ReadUInt16(data, offset);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            var length = ReadUInt16(data, offset + 8);
            offset += 10;
            Ensure(data, offset, length);
            var end = offset + length;
            if (type == 33 && length >= 7)
            {
                var priority = ReadUInt16(data, offset);
                var weight = ReadUInt16(data, offset + 2);
                var port = ReadUInt16(data, offset + 4);
                offset += 6;
                var target = ReadName(data, ref offset);
                if (offset > end) throw new InvalidDataException("DNS SRV RDATA 越界");
                result.Add(new SrvRecord(priority, weight, port, target, ttl));
            }
            offset = end;
        }
        return result.OrderBy(x => x.Priority).ThenByDescending(x => x.Weight).ThenBy(x => x.Target, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<AddressRecord> ParseAddressResponse(byte[] data, ushort expectedId, ushort expectedType)
    {
        if (data.Length < 12 || ReadUInt16(data, 0) != expectedId) throw new InvalidDataException("DNS 地址响应无效");
        var flags = ReadUInt16(data, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x0200) != 0 || (flags & 0x000F) != 0)
            throw new InvalidDataException($"DNS 地址查询失败，flags={flags:x4}");
        var questions = ReadUInt16(data, 4);
        var recordCount = ReadUInt16(data, 6) + ReadUInt16(data, 8) + ReadUInt16(data, 10);
        var offset = 12;
        for (var i = 0; i < questions; i++) { ReadName(data, ref offset); Ensure(data, offset, 4); offset += 4; }
        var result = new List<AddressRecord>();
        for (var i = 0; i < recordCount; i++)
        {
            ReadName(data, ref offset);
            Ensure(data, offset, 10);
            var type = ReadUInt16(data, offset);
            var @class = ReadUInt16(data, offset + 2);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            var length = ReadUInt16(data, offset + 8);
            offset += 10;
            Ensure(data, offset, length);
            if (@class == 1 && type == expectedType && (length == 4 || length == 16))
                result.Add(new AddressRecord(new IPAddress(data.AsSpan(offset, length)), ttl));
            offset += length;
        }
        return result;
    }

    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            Ensure(data, cursor, 1);
            var length = data[cursor++];
            if (length == 0) { if (!jumped) offset = cursor; break; }
            if ((length & 0xC0) == 0xC0)
            {
                Ensure(data, cursor, 1);
                var pointer = ((length & 0x3F) << 8) | data[cursor++];
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                if (++jumps > 16) throw new InvalidDataException("DNS 名称压缩指针循环");
                continue;
            }
            if ((length & 0xC0) != 0) throw new InvalidDataException("DNS 标签长度无效");
            Ensure(data, cursor, length);
            labels.Add(System.Text.Encoding.ASCII.GetString(data, cursor, length));
            cursor += length;
            if (!jumped) offset = cursor;
        }
        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        Ensure(data, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static void Ensure(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count) throw new InvalidDataException("DNS 响应被截断");
    }

    private static string SanitizeId(string value) =>
        new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    /// <summary>DNS 地址记录及权威 TTL；TTL 为空表示地址来自不提供 TTL 的系统解析器。</summary>
    internal sealed record AddressRecord(IPAddress Address, uint? Ttl);
    private sealed record SrvRecord(ushort Priority, ushort Weight, ushort Port, string Target, uint Ttl);
}
