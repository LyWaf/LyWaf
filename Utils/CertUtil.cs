using System.Security.Cryptography.X509Certificates;

namespace LyWaf.Utils;

public static class CertUtils
{
    public static X509Certificate2 LoadFromFile(string pemOrPfx, string? keyOrPass)
    {
        var extension = Path.GetExtension(pemOrPfx).ToLower();
        switch (extension)
        {
            case ".pem":
                {
                    var cert = X509Certificate2.CreateFromPemFile(pemOrPfx, keyOrPass);
                    return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
                }
            case ".pfx" or ".p12":
                {
                    return X509CertificateLoader.LoadPkcs12FromFile(pemOrPfx, keyOrPass);
                }
            case ".cer" or ".crt":
                {
                    return X509CertificateLoader.LoadCertificateFromFile(pemOrPfx);
                }
            default:
                throw new Exception("未支持的证书后缀名称");
        }
    }
}