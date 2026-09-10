using System.Security.Cryptography;
using LyWaf.Services.Backup;
using Xunit;

namespace LyWaf.Tests;

public sealed class EncryptedBackupStreamTests
{
    private const string Password = "backup-test-password";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(180000)]
    public async Task RoundTripsMultipleBlockSizes(int size)
    {
        var content = RandomNumberGenerator.GetBytes(size);
        using var encrypted = new MemoryStream();
        await EncryptedBackupStream.EncryptAsync(new MemoryStream(content), encrypted, Password, default);
        encrypted.Position = 0;
        using var restored = new MemoryStream();
        await EncryptedBackupStream.DecryptAsync(encrypted, restored, Password, default);
        Assert.Equal(content, restored.ToArray());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("tamper")]
    [InlineData("truncate")]
    [InlineData("append")]
    [InlineData("reorder")]
    public async Task RejectsUnauthenticatedData(string change)
    {
        using var encrypted = new MemoryStream();
        await EncryptedBackupStream.EncryptAsync(new MemoryStream(new byte[140000]), encrypted, Password, default);
        var data = encrypted.ToArray();
        if (change == "tamper") data[40] ^= 1;
        if (change == "truncate") data = data[..^20];
        if (change == "append") data = [.. data, 1];
        if (change == "reorder")
        {
            const int frame = 65536 + 20;
            var first = data.AsSpan(32, frame).ToArray();
            data.AsSpan(32 + frame, frame).CopyTo(data.AsSpan(32));
            first.CopyTo(data, 32 + frame);
        }
        using var restored = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() => EncryptedBackupStream.DecryptAsync(new MemoryStream(data), restored,
            change == "password" ? "a-different-password" : Password, default));
    }
}
