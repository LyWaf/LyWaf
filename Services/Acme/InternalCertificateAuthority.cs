using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LyWaf.Services.Acme;

/// <summary>持久化本地根密钥并为显式启用的开发环境签发域名证书。</summary>
internal sealed class InternalCertificateAuthority(AcmeOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<X509Certificate2> GetOrCreateAsync(string domain, CancellationToken token)
    {
        var safeName = SafeFileName(domain);
        var directory = Path.GetFullPath(options.InternalCaPath);
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, $"{safeName}.pem");
        var keyPath = Path.Combine(directory, $"{safeName}.key");
        if (File.Exists(certificatePath) && File.Exists(keyPath))
            return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);

        await _gate.WaitAsync(token);
        try
        {
            if (File.Exists(certificatePath) && File.Exists(keyPath))
                return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            var (root, rootKey) = await LoadOrCreateRootAsync(directory, token);
            using (root)
            using (rootKey)
            using (var leafKey = RSA.Create(2048))
            {
                var request = new CertificateRequest($"CN={domain}", leafKey, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(domain);
                request.CertificateExtensions.Add(san.Build());
                var serial = RandomNumberGenerator.GetBytes(16);
                using var signed = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddDays(Math.Clamp(options.InternalCertificateDays, 1, 825)), serial);
                await File.WriteAllTextAsync(certificatePath, signed.ExportCertificatePem(), token);
                await File.WriteAllTextAsync(keyPath, leafKey.ExportPkcs8PrivateKeyPem(), token);
                RestrictPrivateKey(keyPath);
            }
            return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        }
        finally { _gate.Release(); }
    }

    private static async Task<(X509Certificate2 Certificate, RSA Key)> LoadOrCreateRootAsync(string directory,
        CancellationToken token)
    {
        var certificatePath = Path.Combine(directory, "root-ca.pem");
        var keyPath = Path.Combine(directory, "root-ca.key");
        if (!File.Exists(certificatePath) || !File.Exists(keyPath))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=LyWaf Internal Development CA", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            await File.WriteAllTextAsync(certificatePath, root.ExportCertificatePem(), token);
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), token);
            RestrictPrivateKey(keyPath);
        }
        var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(keyPath, token));
        var certificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(certificatePath, token));
        var withKey = certificate.CopyWithPrivateKey(rsa);
        certificate.Dispose();
        return (withKey, rsa);
    }

    private static string SafeFileName(string domain)
    {
        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Length is 0 or > 253 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') || value.Contains('\\')) throw new ArgumentException("Invalid certificate domain.", nameof(domain));
        return value.Replace("*", "_wildcard_", StringComparison.Ordinal);
    }

    private static void RestrictPrivateKey(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
