using System.Buffers.Binary;
using System.Text;

namespace LyWaf.Services.StreamServer;

/// <summary>从有界 TLS ClientHello 前缀中提取明文 SNI，不执行 TLS 终止。</summary>
internal static class TlsClientHelloParser
{
    public const int MaxClientHelloBytes = 64 * 1024;

    public static int RequiredRecordBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != 22) return 0;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[3..5]);
        var total = 5 + length;
        return total <= MaxClientHelloBytes ? total : 0;
    }

    public static bool TryGetServerName(ReadOnlySpan<byte> data, out string? serverName)
    {
        serverName = null;
        var recordLength = RequiredRecordBytes(data);
        if (recordLength == 0 || data.Length < recordLength) return false;
        var record = data[5..recordLength];
        if (record.Length < 4 || record[0] != 1) return false;
        var handshakeLength = (record[1] << 16) | (record[2] << 8) | record[3];
        if (handshakeLength + 4 > record.Length) return false;
        var hello = record.Slice(4, handshakeLength);
        var offset = 34; // legacy_version + random
        if (!SkipVector8(hello, ref offset) || !SkipVector16(hello, ref offset) || !SkipVector8(hello, ref offset)) return false;
        if (!ReadUInt16(hello, ref offset, out var extensionsLength) || offset + extensionsLength > hello.Length) return false;
        var end = offset + extensionsLength;
        while (offset + 4 <= end)
        {
            if (!ReadUInt16(hello, ref offset, out var type) || !ReadUInt16(hello, ref offset, out var length) || offset + length > end) return false;
            if (type != 0)
            {
                offset += length;
                continue;
            }
            var extensionEnd = offset + length;
            if (!ReadUInt16(hello, ref offset, out var namesLength) || offset + namesLength > extensionEnd) return false;
            var namesEnd = offset + namesLength;
            while (offset + 3 <= namesEnd)
            {
                var nameType = hello[offset++];
                if (!ReadUInt16(hello, ref offset, out var nameLength) || offset + nameLength > namesEnd) return false;
                if (nameType == 0 && nameLength > 0)
                {
                    var name = Encoding.ASCII.GetString(hello.Slice(offset, nameLength));
                    if (Uri.CheckHostName(name) is UriHostNameType.Dns)
                    {
                        serverName = name.ToLowerInvariant();
                        return true;
                    }
                }
                offset += nameLength;
            }
            return false;
        }
        return false;
    }

    private static bool SkipVector8(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length) return false;
        var length = data[offset++];
        if (offset + length > data.Length) return false;
        offset += length;
        return true;
    }

    private static bool SkipVector16(ReadOnlySpan<byte> data, ref int offset)
    {
        if (!ReadUInt16(data, ref offset, out var length) || offset + length > data.Length) return false;
        offset += length;
        return true;
    }

    private static bool ReadUInt16(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        if (offset + 2 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt16BigEndian(data[offset..(offset + 2)]);
        offset += 2;
        return true;
    }
}
