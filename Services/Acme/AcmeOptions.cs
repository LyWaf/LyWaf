namespace LyWaf.Services.Acme;

/// <summary>
/// ACME/Let's Encrypt 证书自动申请配置
/// </summary>
public class AcmeOptions
{
    /// <summary>
    /// 是否启用 ACME 自动证书管理
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// ACME 服务器地址
    /// 生产环境: https://acme-v02.api.letsencrypt.org/directory
    /// 测试环境: https://acme-staging-v02.api.letsencrypt.org/directory
    /// </summary>
    public string DirectoryUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// 联系邮箱（用于 Let's Encrypt 通知）
    /// </summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// 是否同意服务条款
    /// </summary>
    public bool AcceptTermsOfService { get; set; } = false;

    /// <summary>
    /// 需要申请证书的域名列表
    /// </summary>
    public List<string> Domains { get; set; } = [];

    /// <summary>
    /// 证书存储目录
    /// </summary>
    public string CertificatePath { get; set; } = "certs";

    /// <summary>
    /// ACME 账户密钥文件路径
    /// </summary>
    public string AccountKeyPath { get; set; } = "certs/account.pem";

    /// <summary>
    /// 证书有效期剩余天数小于此值时自动续期
    /// </summary>
    public int RenewBeforeDays { get; set; } = 30;

    /// <summary>
    /// 检查证书续期的间隔（小时）
    /// </summary>
    public int CheckIntervalHours { get; set; } = 12;

    /// <summary>
    /// 是否使用测试环境（staging）
    /// 测试时建议开启，避免触发 Let's Encrypt 速率限制
    /// </summary>
    public bool UseStaging { get; set; } = false;
}

/// <summary>
/// 域名证书信息
/// </summary>
public class DomainCertificate
{
    /// <summary>
    /// 主域名
    /// </summary>
    public string Domain { get; set; } = "";

    /// <summary>
    /// 证书 PEM 文件路径
    /// </summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>
    /// 私钥 PEM 文件路径
    /// </summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>
    /// 证书过期时间
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 是否需要续期
    /// </summary>
    public bool NeedsRenewal(int renewBeforeDays)
    {
        return DateTime.UtcNow.AddDays(renewBeforeDays) >= ExpiresAt;
    }
}
