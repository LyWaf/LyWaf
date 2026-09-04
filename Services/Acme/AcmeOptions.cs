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

    /// <summary>按顺序尝试的 ACME Directory；为空时使用 DirectoryUrl。</summary>
    public List<string> DirectoryUrls { get; set; } = [];

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

    /// <summary>是否允许在 TLS 握手遇到缺失证书时按需签发。</summary>
    public bool OnDemandEnabled { get; set; }

    /// <summary>按需签发允许的精确域名或 `*.example.com` 通配后缀。</summary>
    public List<string> OnDemandAllowedDomains { get; set; } = [];

    /// <summary>单实例每小时最多启动的按需签发次数。</summary>
    public int OnDemandMaxRequestsPerHour { get; set; } = 10;

    /// <summary>等待按需签发完成的最长秒数。</summary>
    public int OnDemandTimeoutSeconds { get; set; } = 20;

    /// <summary>是否在没有公网 CA 证书时签发本地开发证书。</summary>
    public bool InternalCaEnabled { get; set; }

    /// <summary>内部 CA 根证书和私钥目录。</summary>
    public string InternalCaPath { get; set; } = "certs/internal-ca";

    /// <summary>内部 CA 叶证书有效天数。</summary>
    public int InternalCertificateDays { get; set; } = 30;

    /// <summary>跨进程签发锁目录；多节点协调要求该目录位于支持排他文件锁的共享存储。</summary>
    public string CoordinationLockPath { get; set; } = "certs/locks";

    /// <summary>等待同一域名签发锁的最长秒数。</summary>
    public int CoordinationLockTimeoutSeconds { get; set; } = 30;

    /// <summary>是否周期性使用系统证书链执行在线吊销状态检查。</summary>
    public bool CheckRevocationOnline { get; set; }

    /// <summary>在线证书状态检查的网络超时秒数。</summary>
    public int RevocationCheckTimeoutSeconds { get; set; } = 10;
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

    /// <summary>证书来源：acme、internal 或 file。</summary>
    public string Source { get; set; } = "file";

    /// <summary>最近证书链/吊销状态：unknown、valid、revoked 或 error。</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>最近状态检查的 UTC 时间。</summary>
    public DateTimeOffset? StatusCheckedAt { get; set; }

    /// <summary>
    /// 是否需要续期
    /// </summary>
    public bool NeedsRenewal(int renewBeforeDays)
    {
        return DateTime.UtcNow.AddDays(renewBeforeDays) >= ExpiresAt;
    }
}
