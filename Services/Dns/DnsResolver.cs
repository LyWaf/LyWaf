using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LyWaf.Services.Dns;

/// <summary>
/// 轻量级 DNS 解析器，向指定 DNS 服务器发送 UDP A/AAAA 查询
/// 仅实现最基本的 DNS 协议，用于自定义 DNS 服务器场景
/// </summary>
internal static class DnsResolver
{
    /// <summary>
    /// 向指定 DNS 服务器查询域名的 A 记录（IPv4）
    /// </summary>
    /// <param name="hostname">要查询的域名</param>
    /// <param name="dnsServer">DNS 服务器地址（IP:Port）</param>
    /// <param name="timeoutMs">查询超时（毫秒，默认 3000）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>解析到的 IP 地址数组（可能为空）</returns>
    public static async Task<IPAddress[]> QueryAsync(
        string hostname,
        IPEndPoint dnsServer,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        var query = BuildQuery(hostname, QType.A);

        using var udp = new UdpClient();
        await udp.SendAsync(query, dnsServer, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var result = await udp.ReceiveAsync(cts.Token);
        return ParseResponse(result.Buffer);
    }

    /// <summary>
    /// 解析 DNS 服务器地址字符串（支持 "IP" 或 "IP:Port" 格式）
    /// </summary>
    public static bool TryParseDnsServer(string server, out IPEndPoint endpoint)
    {
        endpoint = default!;
        if (string.IsNullOrWhiteSpace(server))
            return false;

        // 处理 [IPv6]:Port 格式
        if (server.StartsWith('['))
        {
            var closeBracket = server.IndexOf(']');
            if (closeBracket < 0) return false;
            var ipPart = server[1..closeBracket];
            if (!IPAddress.TryParse(ipPart, out var ip6)) return false;

            var port = 53;
            if (closeBracket + 1 < server.Length && server[closeBracket + 1] == ':')
            {
                if (!int.TryParse(server[(closeBracket + 2)..], out port)) return false;
            }
            endpoint = new IPEndPoint(ip6, port);
            return true;
        }

        // 处理 IP:Port 或纯 IP 格式
        var lastColon = server.LastIndexOf(':');
        if (lastColon > 0 && !server.Contains('.') == false)
        {
            // 可能是 IPv4:Port
            var parts = server.Split(':');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip4) && int.TryParse(parts[1], out var port4))
            {
                endpoint = new IPEndPoint(ip4, port4);
                return true;
            }
        }

        // 纯 IP 地址（v4 或 v6）
        if (IPAddress.TryParse(server, out var ip))
        {
            endpoint = new IPEndPoint(ip, 53);
            return true;
        }

        // 最后尝试 host:port 格式（host 部分必须是 IP）
        if (lastColon > 0)
        {
            var hostPart = server[..lastColon];
            var portPart = server[(lastColon + 1)..];
            if (IPAddress.TryParse(hostPart, out var ipFinal) && int.TryParse(portPart, out var portFinal))
            {
                endpoint = new IPEndPoint(ipFinal, portFinal);
                return true;
            }
        }

        return false;
    }

    // ── DNS 常量 ──

    private enum QType : ushort
    {
        A = 1,
        AAAA = 28
    }

    // ── 构建查询包 ──

    private static byte[] BuildQuery(string hostname, QType qtype)
    {
        using var ms = new MemoryStream(64);
        Span<byte> buf = stackalloc byte[2];

        // Header (12 bytes)
        var id = (ushort)Random.Shared.Next(0, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(buf, id);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0x0100); // Flags: 标准查询, RD=1（递归查询）
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, 1);      // QDCOUNT = 1
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0);      // ANCOUNT = 0
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0);      // NSCOUNT = 0
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, 0);      // ARCOUNT = 0
        ms.Write(buf);

        // Question: 域名标签编码
        foreach (var label in hostname.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        ms.WriteByte(0); // 根标签

        // QTYPE + QCLASS
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)qtype);
        ms.Write(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf, 1); // IN class
        ms.Write(buf);

        return ms.ToArray();
    }

    // ── 解析响应包 ──

    private static IPAddress[] ParseResponse(byte[] data)
    {
        if (data.Length < 12)
            return [];

        // 检查 RCODE（低 4 位）
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        var rcode = flags & 0x0F;
        if (rcode != 0)
            return []; // 非 0 表示查询失败（NXDOMAIN、SERVFAIL 等）

        var qdcount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        var ancount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));

        // 跳过 Question 段
        var offset = 12;
        for (var i = 0; i < qdcount; i++)
        {
            offset = SkipName(data, offset);
            offset += 4; // QTYPE + QCLASS
        }

        // 解析 Answer 段
        var addresses = new List<IPAddress>();
        for (var i = 0; i < ancount && offset < data.Length; i++)
        {
            offset = SkipName(data, offset);
            if (offset + 10 > data.Length) break;

            var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            offset += 2; // TYPE
            offset += 2; // CLASS
            offset += 4; // TTL
            var rdlength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            offset += 2; // RDLENGTH

            if (offset + rdlength > data.Length) break;

            if (type == (ushort)QType.A && rdlength == 4)
            {
                // A 记录：4 字节 IPv4
                addresses.Add(new IPAddress(data.AsSpan(offset, 4)));
            }
            else if (type == (ushort)QType.AAAA && rdlength == 16)
            {
                // AAAA 记录：16 字节 IPv6
                addresses.Add(new IPAddress(data.AsSpan(offset, 16)));
            }

            offset += rdlength;
        }

        return [.. addresses];
    }

    /// <summary>
    /// 跳过 DNS 名称字段（支持压缩指针 0xC0）
    /// </summary>
    private static int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            var len = data[offset];
            if (len == 0)
            {
                return offset + 1; // 根标签
            }
            if ((len & 0xC0) == 0xC0)
            {
                return offset + 2; // 压缩指针（2 字节）
            }
            offset += 1 + len; // 标签长度 + 标签内容
        }
        return offset;
    }
}
