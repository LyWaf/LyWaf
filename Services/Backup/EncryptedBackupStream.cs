using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LyWaf.Services.Backup;

/// <summary>分块认证的备份容器。每块最多 64 KiB，末尾认证空块防止截断，序号防止重排。</summary>
public static class EncryptedBackupStream
{
    private static readonly byte[] Magic = "LYWAFBK1"u8.ToArray();
    private const int BlockSize = 65536;
    private const int HeaderSize = 32;
    public const long MaximumPlaintextBytes = 4L * 1024 * 1024 * 1024;

    public static async Task EncryptAsync(Stream source, Stream destination, string password, CancellationToken token)
    {
        ValidatePassword(password);
        var header = RandomNumberGenerator.GetBytes(HeaderSize);
        Magic.CopyTo(header, 0);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(8, 16), 210000, HashAlgorithmName.SHA256, 32);
        var clear = new byte[BlockSize];
        try
        {
            using var aes = new AesGcm(key, 16);
            await destination.WriteAsync(header, token);
            var encrypted = new byte[BlockSize];
            var tag = new byte[16];
            var lengthBytes = new byte[4];
            long total = 0;
            for (uint index = 0; ; index++)
            {
                var count = await source.ReadAtLeastAsync(clear, BlockSize, false, token);
                total += count;
                if (total > MaximumPlaintextBytes) throw new InvalidDataException("备份包超过 4 GiB 限制");
                BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, count);
                aes.Encrypt(Nonce(header, index), clear.AsSpan(0, count), encrypted.AsSpan(0, count), tag, AssociatedData(header, index, count));
                await destination.WriteAsync(lengthBytes, token);
                await destination.WriteAsync(encrypted.AsMemory(0, count), token);
                await destination.WriteAsync(tag, token);
                if (count == 0) break;
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    // 输出只能指向暂存文件；只有认证终止块通过后，调用方才能将其用于恢复。
    public static async Task DecryptAsync(Stream source, Stream stagingDestination, string password, CancellationToken token)
    {
        ValidatePassword(password);
        var header = new byte[HeaderSize];
        await source.ReadExactlyAsync(header, token);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("不支持的备份格式或版本");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(8, 16), 210000, HashAlgorithmName.SHA256, 32);
        var clear = new byte[BlockSize];
        try
        {
            using var aes = new AesGcm(key, 16);
            var encrypted = new byte[BlockSize];
            var tag = new byte[16];
            var lengthBytes = new byte[4];
            long total = 0;
            for (uint index = 0; ; index++)
            {
                await source.ReadExactlyAsync(lengthBytes, token);
                var count = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (count < 0 || count > BlockSize || (total += count) > MaximumPlaintextBytes)
                    throw new InvalidDataException("备份数据块大小无效");
                await source.ReadExactlyAsync(encrypted.AsMemory(0, count), token);
                await source.ReadExactlyAsync(tag, token);
                aes.Decrypt(Nonce(header, index), encrypted.AsSpan(0, count), tag, clear.AsSpan(0, count), AssociatedData(header, index, count));
                if (count == 0)
                {
                    if (await source.ReadAsync(lengthBytes.AsMemory(0, 1), token) != 0)
                        throw new InvalidDataException("备份结束标记后存在多余数据");
                    break;
                }
                await stagingDestination.WriteAsync(clear.AsMemory(0, count), token);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] Nonce(byte[] header, uint index)
    {
        var nonce = new byte[12];
        header.AsSpan(24, 8).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] AssociatedData(byte[] header, uint index, int count)
    {
        var data = new byte[HeaderSize + 8];
        header.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(HeaderSize), index);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(HeaderSize + 4), count);
        return data;
    }

    private static void ValidatePassword(string password)
    {
        if (password is not { Length: >= 12 and <= 1024 })
            throw new ArgumentException("备份密码长度须为 12 至 1024 字符", nameof(password));
    }
}
