using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// Pcap 证书提供器
/// 管理根 CA 证书并为每个域名动态签发证书，用于 HTTPS 抓包
/// </summary>
public class PcapCertProvider
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly string _caDir;
    private readonly string _caPfxPath;
    private readonly string _caCrtPath;
    private readonly string _caCrlPath;

    private X509Certificate2? _caCert;
    private readonly ConcurrentDictionary<string, X509Certificate2> _hostCerts = new();

    /// <summary>
    /// CRL 分发点 URL（由外部设置，嵌入到签发的证书中）
    /// </summary>
    public string? CrlUrl { get; set; }

    public PcapCertProvider()
    {
        _caDir = Path.Combine(AppContext.BaseDirectory, "datas");
        _caPfxPath = Path.Combine(_caDir, "proxy-ca.pfx");
        _caCrtPath = Path.Combine(_caDir, "proxy-ca.crt");
        _caCrlPath = Path.Combine(_caDir, "proxy-ca.crl");
    }

    /// <summary>
    /// 初始化 CA 证书（加载或生成）
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(_caDir);

        // 清除已缓存的域名证书（新证书将包含 CRL 分发点）
        _hostCerts.Clear();

        if (File.Exists(_caPfxPath))
        {
            _caCert = X509CertificateLoader.LoadPkcs12FromFile(_caPfxPath, null, X509KeyStorageFlags.Exportable);
            _logger.Info("已加载 Pcap CA 证书: {Subject}, 有效期至 {NotAfter:yyyy-MM-dd}", _caCert.Subject, _caCert.NotAfter);
        }
        else
        {
            _caCert = GenerateCaCertificate();
            _logger.Info("已生成新的 Pcap CA 证书: {Path}", _caCrtPath);
        }

        // 生成空 CRL（如果不存在或已过期）
        GenerateEmptyCrl();

        _logger.Info("Pcap 抓包提示: 请将 CA 证书安装到受信任的根证书颁发机构: {Path}", _caCrtPath);
    }

    /// <summary>
    /// 获取指定域名的证书（缓存 + 动态签发）
    /// </summary>
    public X509Certificate2 GetCertForHost(string hostname)
    {
        return _hostCerts.GetOrAdd(hostname, host =>
        {
            var cert = GenerateHostCertificate(host);
            _logger.Debug("已签发域名证书: {Host}", host);
            return cert;
        });
    }

    /// <summary>
    /// CA 证书路径（供用户下载安装）
    /// </summary>
    public string CaCertPath => _caCrtPath;

    /// <summary>
    /// CRL 文件路径
    /// </summary>
    public string CaCrlPath => _caCrlPath;

    /// <summary>
    /// 生成自签名根 CA 证书
    /// </summary>
    private X509Certificate2 GenerateCaCertificate()
    {
        using var rsa = RSA.Create(2048);

        var subject = new X500DistinguishedName("CN=LyWaf Proxy CA, O=LyWaf, OU=Proxy");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // CA 基本约束
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: true,
                pathLengthConstraint: 1,
                critical: true));

        // 密钥用途
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                critical: true));

        // SKI
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);

        var cert = request.CreateSelfSigned(notBefore, notAfter);

        // 导出 PFX（带私钥）
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(_caPfxPath, pfxBytes);

        // 导出 CRT（公钥，供用户安装）
        var crtPem = cert.ExportCertificatePem();
        File.WriteAllText(_caCrtPath, crtPem);

        // 重新加载以确保私钥可用
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// 用 CA 签发域名证书
    /// </summary>
    private X509Certificate2 GenerateHostCertificate(string hostname)
    {
        if (_caCert == null)
            throw new InvalidOperationException("CA 证书未初始化");

        using var rsa = RSA.Create(2048);

        var subject = new X500DistinguishedName($"CN={hostname}");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // 非 CA
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        // 密钥用途
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        // 增强密钥用途 - 服务器认证
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], // serverAuth
                false));

        // SAN (Subject Alternative Name)
        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(hostname, out var ip))
        {
            sanBuilder.AddIpAddress(ip);
        }
        else
        {
            sanBuilder.AddDnsName(hostname);
        }
        request.CertificateExtensions.Add(sanBuilder.Build());

        // SKI
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // AKI (Authority Key Identifier) - 关联到 CA
        var caSkiExt = _caCert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
        if (caSkiExt != null)
        {
            request.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(caSkiExt));
        }

        // CRL 分发点 - 让客户端能验证证书吊销状态
        if (!string.IsNullOrEmpty(CrlUrl))
        {
            request.CertificateExtensions.Add(BuildCdpExtension(CrlUrl));
        }

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);

        // 序列号
        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F; // 确保为正数

        // 用 CA 签发
        using var caPrivateKey = _caCert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("CA 证书缺少私钥");

        var cert = request.Create(_caCert, notBefore, notAfter, serialNumber);

        // 合并私钥
        var certWithKey = cert.CopyWithPrivateKey(rsa);

        // 导出再导入，确保 Windows 上私钥可用
        var pfxBytes = certWithKey.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// 生成空的 CRL（Certificate Revocation List）
    /// 供客户端验证证书吊销状态
    /// </summary>
    private void GenerateEmptyCrl()
    {
        if (_caCert == null) return;

        try
        {
            var crlBuilder = new CertificateRevocationListBuilder();
            var now = DateTimeOffset.UtcNow;

            // 使用 CA 私钥签发空 CRL，有效期 30 天
            var crlBytes = crlBuilder.Build(
                _caCert,
                crlNumber: 1,
                nextUpdate: now.AddDays(30),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1,
                thisUpdate: now);

            File.WriteAllBytes(_caCrlPath, crlBytes);
            _logger.Info("已生成 CRL: {Path}", _caCrlPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "生成 CRL 失败");
        }
    }

    /// <summary>
    /// 构建 CRL Distribution Points 扩展
    /// OID: 2.5.29.31
    /// </summary>
    private static X509Extension BuildCdpExtension(string crlUrl)
    {
        // CRL Distribution Points 的 ASN.1 结构:
        // SEQUENCE {
        //   SEQUENCE {                    -- DistributionPoint
        //     [0] {                       -- distributionPoint
        //       [0] {                     -- fullName
        //         [6] "http://..."        -- uniformResourceIdentifier (IA5String, context tag 6)
        //       }
        //     }
        //   }
        // }
        var urlBytes = System.Text.Encoding.ASCII.GetBytes(crlUrl);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        // 外层 SEQUENCE (CRLDistributionPoints)
        writer.PushSequence();
        {
            // DistributionPoint SEQUENCE
            writer.PushSequence();
            {
                // [0] distributionPoint (context, constructed)
                writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                {
                    // [0] fullName (context, constructed)
                    writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                    {
                        // [6] uniformResourceIdentifier (context, primitive)
                        writer.WriteCharacterString(
                            UniversalTagNumber.IA5String,
                            crlUrl,
                            new Asn1Tag(TagClass.ContextSpecific, 6, isConstructed: false));
                    }
                    writer.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                }
                writer.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            }
            writer.PopSequence();
        }
        writer.PopSequence();

        return new X509Extension("2.5.29.31", writer.Encode(), critical: false);
    }
}
