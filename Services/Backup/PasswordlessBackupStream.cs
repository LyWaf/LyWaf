namespace LyWaf.Services.Backup;

/// <summary>免密码备份容器；只提供格式识别和容量限制，不提供加密或来源认证。</summary>
internal static class PasswordlessBackupStream
{
    internal static ReadOnlySpan<byte> Magic => "LYWAFBP1"u8;

    internal static async Task WriteAsync(Stream source, Stream destination, CancellationToken token)
    {
        await destination.WriteAsync(Magic.ToArray(), token);
        await CopyAsync(source, destination, token);
    }

    internal static async Task ReadAsync(Stream source, Stream destination, CancellationToken token)
    {
        var header = new byte[8];
        await source.ReadExactlyAsync(header, token);
        if (!header.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("不是免密码备份；旧加密备份需要通过 API 提供原密码恢复");
        await CopyAsync(source, destination, token);
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            // Allow ZIP metadata/compression overhead above the existing data limit.
            if ((total += count) > EncryptedBackupStream.MaximumPlaintextBytes + 64 * 1024 * 1024)
                throw new InvalidDataException("备份容器超过容量限制");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
