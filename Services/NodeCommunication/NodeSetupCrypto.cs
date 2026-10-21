using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Services.NodeCommunication;

internal static class NodeSetupCrypto
{
    internal static bool ValidAuthority(NodeHubAuthority authority) => ValidPublicKey(authority.ReleasePublicKeyPem)
        && ValidChallengeSecret(authority.ChallengeSecret) && ValidTrustDomain(authority.ReleaseTrustDomain);

    internal static string NewPrivateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    internal static string PublicKey(string privatePem)
    {
        if (!ValidPrivateKey(privatePem)) throw new InvalidDataException("Invalid controller signing key");
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    internal static bool ValidPrivateKey(string? pem)
    {
        if (pem is not { Length: > 0 and <= 2048 } || !pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.KeySize == 256 && key.ExportParameters(true).D is not null
                && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7";
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { return false; }
    }

    internal static bool ValidPublicKey(string? pem)
    {
        if (pem is not { Length: > 0 and <= 1024 } || !pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7";
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { return false; }
    }

    internal static bool ValidChallengeSecret(string? secret) => secret is { Length: > 0 and <= 1024 }
        && Encoding.UTF8.GetByteCount(secret) is >= 32 and <= 1024;
    internal static bool ValidTrustDomain(string? domain) => domain is { Length: > 0 and <= 128 } && !domain.Any(char.IsControl);
}
