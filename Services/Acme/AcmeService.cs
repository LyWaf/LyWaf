using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.Acme;

/// <summary>
/// ACME 服务接口
/// </summary>
public interface IAcmeService
{
    /// <summary>
    /// 获取 HTTP-01 挑战响应
    /// </summary>
    string? GetChallengeResponse(string token);

    /// <summary>
    /// 为域名申请证书
    /// </summary>
    Task<X509Certificate2?> RequestCertificateAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取域名的证书
    /// </summary>
    X509Certificate2? GetCertificate(string domain);

    /// <summary>
    /// 启动证书管理（后台任务）
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 停止证书管理
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// ACME 服务实现
/// 使用 Certes 库实现 Let's Encrypt 证书自动申请
/// </summary>
public class AcmeService : IAcmeService, IHostedService, IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly AcmeOptions _options;
    private readonly ConcurrentDictionary<string, string> _pendingChallenges = new();
    private readonly ConcurrentDictionary<string, X509Certificate2> _certificates = new();
    private readonly ConcurrentDictionary<string, DomainCertificate> _certificateInfos = new();

    private AcmeContext? _acmeContext;
    private Timer? _renewalTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AcmeService(IOptionsMonitor<AcmeOptions> options)
    {
        _options = options.CurrentValue;
    }

    /// <summary>
    /// 获取 HTTP-01 挑战响应
    /// </summary>
    public string? GetChallengeResponse(string token)
    {
        _pendingChallenges.TryGetValue(token, out var response);
        return response;
    }

    /// <summary>
    /// 获取域名的证书
    /// </summary>
    public X509Certificate2? GetCertificate(string domain)
    {
        // 尝试精确匹配
        if (_certificates.TryGetValue(domain, out var cert))
            return cert;

        // 尝试通配符匹配
        var parts = domain.Split('.');
        if (parts.Length >= 2)
        {
            var wildcardDomain = "*." + string.Join('.', parts.Skip(1));
            if (_certificates.TryGetValue(wildcardDomain, out cert))
                return cert;
        }

        return null;
    }

    /// <summary>
    /// 启动证书管理
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.Info("ACME 证书自动管理未启用");
            return;
        }

        if (!_options.AcceptTermsOfService)
        {
            _logger.Error("必须同意 Let's Encrypt 服务条款才能使用 ACME 功能");
            return;
        }

        if (string.IsNullOrEmpty(_options.Email))
        {
            _logger.Error("必须配置联系邮箱才能使用 ACME 功能");
            return;
        }

        _logger.Info("启动 ACME 证书管理服务, 域名: {Domains}", string.Join(", ", _options.Domains));

        // 确保证书目录存在
        System.IO.Directory.CreateDirectory(_options.CertificatePath);

        // 初始化 ACME 上下文
        await InitializeAcmeContextAsync();

        // 加载现有证书
        await LoadExistingCertificatesAsync();

        // 检查并申请缺失的证书
        await CheckAndRequestCertificatesAsync(cancellationToken);

        // 启动定期续期检查
        _renewalTimer = new Timer(
            async _ => await CheckAndRenewCertificatesAsync(CancellationToken.None),
            null,
            TimeSpan.FromHours(_options.CheckIntervalHours),
            TimeSpan.FromHours(_options.CheckIntervalHours));

        _logger.Info("ACME 证书管理服务已启动");
    }

    /// <summary>
    /// 停止证书管理
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _renewalTimer?.Dispose();
        _logger.Info("ACME 证书管理服务已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 初始化 ACME 上下文
    /// </summary>
    private async Task InitializeAcmeContextAsync()
    {
        var directoryUrl = _options.UseStaging
            ? WellKnownServers.LetsEncryptStagingV2
            : WellKnownServers.LetsEncryptV2;

        if (!string.IsNullOrEmpty(_options.DirectoryUrl))
        {
            directoryUrl = new Uri(_options.DirectoryUrl);
        }

        // 尝试加载现有账户
        if (File.Exists(_options.AccountKeyPath))
        {
            try
            {
                var accountKeyPem = await File.ReadAllTextAsync(_options.AccountKeyPath);
                var accountKey = KeyFactory.FromPem(accountKeyPem);
                _acmeContext = new AcmeContext(directoryUrl, accountKey);
                await _acmeContext.Account();
                _logger.Info("已加载现有 ACME 账户");
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "加载 ACME 账户失败，将创建新账户");
            }
        }

        // 创建新账户
        _acmeContext = new AcmeContext(directoryUrl);
        await _acmeContext.NewAccount(_options.Email, termsOfServiceAgreed: true);

        // 保存账户密钥
        var keyPem = _acmeContext.AccountKey.ToPem();
        var keyDir = Path.GetDirectoryName(_options.AccountKeyPath);
        if (!string.IsNullOrEmpty(keyDir))
        {
            System.IO.Directory.CreateDirectory(keyDir);
        }
        await File.WriteAllTextAsync(_options.AccountKeyPath, keyPem);

        _logger.Info("已创建新 ACME 账户: {Email}", _options.Email);
    }

    /// <summary>
    /// 加载现有证书
    /// </summary>
    private async Task LoadExistingCertificatesAsync()
    {
        foreach (var domain in _options.Domains)
        {
            var safeDomain = domain.Replace("*", "_wildcard_");
            var certPath = Path.Combine(_options.CertificatePath, $"{safeDomain}.pem");
            var keyPath = Path.Combine(_options.CertificatePath, $"{safeDomain}.key");

            if (File.Exists(certPath) && File.Exists(keyPath))
            {
                try
                {
                    var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    
                    // 检查证书是否过期
                    if (cert.NotAfter > DateTime.UtcNow)
                    {
                        _certificates[domain] = cert;
                        _certificateInfos[domain] = new DomainCertificate
                        {
                            Domain = domain,
                            CertificatePath = certPath,
                            PrivateKeyPath = keyPath,
                            ExpiresAt = cert.NotAfter
                        };
                        _logger.Info("已加载域名证书: {Domain}, 过期时间: {ExpiresAt}", domain, cert.NotAfter);
                    }
                    else
                    {
                        _logger.Warn("域名证书已过期: {Domain}", domain);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "加载域名证书失败: {Domain}", domain);
                }
            }
        }
    }

    /// <summary>
    /// 检查并申请缺失的证书
    /// </summary>
    private async Task CheckAndRequestCertificatesAsync(CancellationToken cancellationToken)
    {
        foreach (var domain in _options.Domains)
        {
            if (!_certificates.ContainsKey(domain))
            {
                _logger.Info("域名 {Domain} 没有证书，开始申请", domain);
                await RequestCertificateAsync(domain, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 检查并续期证书
    /// </summary>
    private async Task CheckAndRenewCertificatesAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            foreach (var info in _certificateInfos.Values)
            {
                if (info.NeedsRenewal(_options.RenewBeforeDays))
                {
                    _logger.Info("域名 {Domain} 证书即将过期，开始续期", info.Domain);
                    await RequestCertificateAsync(info.Domain, cancellationToken);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 为域名申请证书
    /// </summary>
    public async Task<X509Certificate2?> RequestCertificateAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (_acmeContext == null)
        {
            _logger.Error("ACME 上下文未初始化");
            return null;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.Info("开始为域名 {Domain} 申请证书", domain);

            // 创建订单
            var order = await _acmeContext.NewOrder(new[] { domain });

            // 获取授权
            var authorizations = await order.Authorizations();

            foreach (var auth in authorizations)
            {
                // 获取 HTTP-01 挑战
                var httpChallenge = await auth.Http();
                if (httpChallenge == null)
                {
                    _logger.Error("无法获取 HTTP-01 挑战: {Domain}", domain);
                    return null;
                }

                // 准备挑战响应
                var token = httpChallenge.Token;
                var keyAuthz = httpChallenge.KeyAuthz;
                _pendingChallenges[token] = keyAuthz;

                _logger.Debug("已准备 HTTP-01 挑战: token={Token}", token);

                try
                {
                    // 验证挑战
                    var challenge = await httpChallenge.Validate();

                    // 等待验证完成
                    var maxRetries = 30;
                    for (var i = 0; i < maxRetries; i++)
                    {
                        await Task.Delay(2000, cancellationToken);

                        var authz = await auth.Resource();
                        if (authz.Status == AuthorizationStatus.Valid)
                        {
                            _logger.Info("域名 {Domain} HTTP-01 验证成功", domain);
                            break;
                        }
                        else if (authz.Status == AuthorizationStatus.Invalid)
                        {
                            _logger.Error("域名 {Domain} HTTP-01 验证失败", domain);
                            return null;
                        }

                        _logger.Debug("等待验证完成... {Status}", authz.Status);
                    }
                }
                finally
                {
                    // 清理挑战
                    _pendingChallenges.TryRemove(token, out _);
                }
            }

            // 生成证书密钥
            var privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);

            // 完成订单
            var certChain = await order.Generate(new CsrInfo
            {
                CommonName = domain
            }, privateKey);

            // 保存证书
            var safeDomain = domain.Replace("*", "_wildcard_");
            var certPath = Path.Combine(_options.CertificatePath, $"{safeDomain}.pem");
            var keyPath = Path.Combine(_options.CertificatePath, $"{safeDomain}.key");

            var certPem = certChain.ToPem();
            var keyPem = privateKey.ToPem();

            await File.WriteAllTextAsync(certPath, certPem, cancellationToken);
            await File.WriteAllTextAsync(keyPath, keyPem, cancellationToken);

            // 加载证书
            var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            _certificates[domain] = cert;
            _certificateInfos[domain] = new DomainCertificate
            {
                Domain = domain,
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                ExpiresAt = cert.NotAfter
            };

            _logger.Info("域名 {Domain} 证书申请成功，过期时间: {ExpiresAt}", domain, cert.NotAfter);

            return cert;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "域名 {Domain} 证书申请失败", domain);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _renewalTimer?.Dispose();
        _semaphore.Dispose();

        foreach (var cert in _certificates.Values)
        {
            cert.Dispose();
        }
        _certificates.Clear();
    }
}
