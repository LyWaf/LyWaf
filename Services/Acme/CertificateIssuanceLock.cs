using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Services.Acme;

/// <summary>通过排他文件句柄协调同一共享目录中的跨进程证书签发。</summary>
internal static class CertificateIssuanceLock
{
    public static async Task<FileStream?> TryAcquireAsync(string directory, string domain, TimeSpan timeout,
        CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(domain.ToLowerInvariant()))).ToLowerInvariant();
        var path = Path.Combine(directory, name + ".lock");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, token);
            }
            catch (UnauthorizedAccessException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, token);
            }
            if (DateTimeOffset.UtcNow >= deadline) return null;
        }
    }
}
