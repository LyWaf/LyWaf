using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Plugins.Sys.HttpUpload;

/// <summary>Validates canonical HMAC metadata and durably consumes nonces before accepting file bytes.</summary>
public static class UploadSignature
{
    private static readonly object Gate = new();

    public static bool Verify(string key, string filename, string size, string timestamp, string nonce,
        string signature, long now, int window, out long bytes)
    {
        bytes = 0;
        if (key.Length < 32 || window is < 1 or > 3600 || !HttpUploadMiddleware.IsSafeName(filename)
            || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out bytes) || bytes < 0
            || bytes.ToString(CultureInfo.InvariantCulture) != size
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds.ToString(CultureInfo.InvariantCulture) != timestamp
            || seconds < now - window || seconds > now + window
            || nonce.Length is < 32 or > 128 || !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || signature.Length != 64 || !signature.All(Uri.IsHexDigit)) return false;
        var message = Encoding.UTF8.GetBytes($"{filename}\n{size}\n{timestamp}\n{nonce}");
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), message);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature));
    }

    public static bool Consume(string directory, string nonce, long now)
    {
        // The maximum permitted window is retained, so later window changes cannot revive a signature.
        lock (Gate)
        {
            directory = Path.GetFullPath(directory);
            HttpUploadMiddleware.CheckDirectory(directory);
            Directory.CreateDirectory(directory);
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.nonce"))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked nonce file.");
                var expiryText = Path.GetFileName(file).Split('.')[0];
                if (long.TryParse(expiryText, out var expiry) && expiry < now) File.Delete(file);
                else count++;
            }
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
            if (Directory.EnumerateFiles(directory, $"*.{digest}.nonce").Any()) return false;
            if (count >= 10000) throw new IOException("Nonce storage is full; rejecting upload.");
            // A fixed per-nonce claim also arbitrates between host processes sharing this directory.
            var claim = Path.Combine(directory, digest + ".claim");
            using var ownership = new FileStream(claim, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            if (Directory.EnumerateFiles(directory, $"*.{digest}.nonce").Any()) return false;
            using var marker = new FileStream(Path.Combine(directory, $"{now + 7200}.{digest}.nonce"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.WriteByte(1);
            marker.Flush(true);
            return true;
        }
    }
}
