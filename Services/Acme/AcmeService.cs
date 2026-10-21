using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.Extensions.Options;
using NLog;
using LyWaf.Services.Metrics;

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

    /// <summary>返回现有证书，或在按需策略允许时发起一次有界签发。</summary>
    Task<X509Certificate2?> GetOrRequestCertificateAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动申请/续期证书（按需初始化 ACME 上下文）
    /// </summary>
    Task<AcmeRequestResult> ManualRequestCertificateAsync(string domain, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取已保存的 ACME 联系邮箱
    /// </summary>
    string? GetSavedEmail();

    /// <summary>获取当前已加载证书的只读状态快照。</summary>
    IReadOnlyList<CertificateStatusSnapshot> GetCertificateStatuses();

    /// <summary>获取配置的 ACME CA 账户摘要，不包含账户密钥内容。</summary>
    IReadOnlyList<AcmeAccountSnapshot> GetAccountStatuses();

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
/// ACME 手动申请结果
/// </summary>
public class AcmeRequestResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Domain { get; set; }
    public string? ExpiresAt { get; set; }
}

/// <summary>供管理端展示的证书到期和吊销检查状态。</summary>
public sealed record CertificateStatusSnapshot(string Domain, DateTime ExpiresAt, int DaysRemaining,
    string Source, string Status, DateTimeOffset? StatusCheckedAt);

/// <summary>供管理端展示的 ACME CA 账户配置状态。</summary>
public sealed record AcmeAccountSnapshot(string DirectoryUrl, bool AccountKeyExists, string? Email);

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
    private readonly ConcurrentDictionary<string, Task<X509Certificate2?>> _onDemandRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _onDemandGate = new();
    private readonly AcmeOnDemandPolicy _onDemandPolicy;
    private readonly InternalCertificateAuthority _internalCa;
    private readonly PrometheusMetrics? _metrics;

    private readonly List<AcmeContext> _acmeContexts = [];
    private Timer? _renewalTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task _renewalWork = Task.CompletedTask;
    private bool _stopping;
    private int _disposed;

    public AcmeService(IOptionsMonitor<AcmeOptions> options, PrometheusMetrics? metrics = null)
    {
        _options = options.CurrentValue;
        _metrics = metrics;
        _onDemandPolicy = new AcmeOnDemandPolicy(_options);
        _internalCa = new InternalCertificateAuthority(_options);
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

    /// <inheritdoc />
    public async Task<X509Certificate2?> GetOrRequestCertificateAsync(string domain, CancellationToken cancellationToken = default)
    {
        var existing = GetCertificate(domain);
        if (existing is not null) return existing;
        var normalized = AcmeOnDemandPolicy.NormalizeDomain(domain);
        if (normalized.Length == 0) return null;
        Task<X509Certificate2?> task;
        lock (_onDemandGate)
        {
            if (_stopping) return null;
            if (!_onDemandRequests.TryGetValue(normalized, out task!))
            {
                if (!_onDemandPolicy.TryAcquire(normalized, DateTimeOffset.UtcNow, out _)) return null;
                task = RequestOnDemandAsync(normalized);
                _onDemandRequests[normalized] = task;
            }
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.OnDemandTimeoutSeconds, 1, 120)));
            return await task.WaitAsync(timeout.Token);
        }
        finally
        {
            if (task.IsCompleted) _onDemandRequests.TryRemove(new KeyValuePair<string, Task<X509Certificate2?>>(normalized, task));
        }
    }

    private async Task<X509Certificate2?> RequestOnDemandAsync(string domain)
    {
        try { return await RequestCertificateAsync(domain, _shutdown.Token); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return null; }
        catch (Exception ex) { _logger.Warn(ex, "域名 {Domain} 按需签发失败", domain); return null; }
    }

    /// <summary>
    /// 启动证书管理
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled && !_options.InternalCaEnabled)
        {
            _logger.Info("自动证书管理未启用");
            return;
        }

        if (_options.Enabled && !_options.AcceptTermsOfService)
        {
            _logger.Error("必须同意 Let's Encrypt 服务条款才能使用 ACME 功能");
            return;
        }

        if (_options.Enabled && string.IsNullOrEmpty(_options.Email))
        {
            _logger.Error("必须配置联系邮箱才能使用 ACME 功能");
            return;
        }

        _logger.Info("启动 ACME 证书管理服务, 域名: {Domains}", string.Join(", ", _options.Domains));

        // 确保证书目录存在
        System.IO.Directory.CreateDirectory(_options.CertificatePath);

        // 初始化 ACME 上下文
        if (_options.Enabled) await InitializeAcmeContextsAsync();

        // 加载现有证书
        await LoadExistingCertificatesAsync();

        // 检查并申请缺失的证书
        await CheckAndRequestCertificatesAsync(cancellationToken);
        if (_options.CheckRevocationOnline)
            foreach (var info in _certificateInfos.Values.Where(static value => value.Source == "acme"))
                await CheckCertificateStatusAsync(info, cancellationToken);

        // 启动定期续期检查
        _renewalTimer = new Timer(
            _ => _ = ScheduleRenewal(),
            null,
            TimeSpan.FromHours(_options.CheckIntervalHours),
            TimeSpan.FromHours(_options.CheckIntervalHours));

        _logger.Info("ACME 证书管理服务已启动");
    }

    /// <summary>
    /// 停止证书管理
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_onDemandGate)
        {
            _stopping = true;
            _renewalTimer?.Dispose();
            pending = _onDemandRequests.Values.Cast<Task>().Append(_renewalWork).ToArray();
        }
        _shutdown.Cancel();
        await Task.WhenAll(pending).WaitAsync(cancellationToken);
        _logger.Info("ACME 证书管理服务已停止");
    }

    /// <summary>
    /// 初始化 ACME 上下文
    /// </summary>
    private async Task InitializeAcmeContextsAsync()
    {
        _acmeContexts.Clear();
        var directories = _options.UseStaging
            ? new[] { WellKnownServers.LetsEncryptStagingV2 }
            : AcmeOnDemandPolicy.GetDirectoryUris(_options);
        for (var index = 0; index < directories.Count; index++)
        {
            var keyPath = AccountKeyPath(index);
            try { _acmeContexts.Add(await CreateContextAsync(directories[index], keyPath, _options.Email)); }
            catch (Exception ex) { _logger.Error(ex, "初始化 ACME CA 失败: {Directory}", directories[index]); }
        }
    }

    private async Task<AcmeContext> CreateContextAsync(Uri directoryUrl, string keyPath, string email)
    {
        if (File.Exists(keyPath))
            try
            {
                var context = new AcmeContext(directoryUrl, KeyFactory.FromPem(await File.ReadAllTextAsync(keyPath)));
                await context.Account();
                _logger.Info("已加载 ACME 账户: {Directory}", directoryUrl.Host);
                return context;
            }
            catch (Exception ex) { _logger.Warn(ex, "加载 ACME 账户失败，将为 {Directory} 创建账户", directoryUrl.Host); }
        var created = new AcmeContext(directoryUrl);
        await created.NewAccount(email, termsOfServiceAgreed: true);
        var keyDir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(keyDir))
            System.IO.Directory.CreateDirectory(keyDir);
        await File.WriteAllTextAsync(keyPath, created.AccountKey.ToPem());
        RestrictPrivateKey(keyPath);
        _logger.Info("已创建 ACME 账户: {Email}, {Directory}", email, directoryUrl.Host);
        return created;
    }

    private string AccountKeyPath(int index) => index == 0 ? _options.AccountKeyPath : $"{_options.AccountKeyPath}.{index}";

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
                            ExpiresAt = cert.NotAfter,
                            Source = "file",
                            Status = "valid"
                        };
                        _metrics?.SetCertificateStatus(domain, cert.NotAfter, "file", "valid");
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
        foreach (var info in _certificateInfos.Values)
        {
            if (info.NeedsRenewal(_options.RenewBeforeDays))
            {
                _logger.Info("域名 {Domain} 证书即将过期，开始续期", info.Domain);
                await RequestCertificateAsync(info.Domain, cancellationToken);
            }
        }
        if (_options.CheckRevocationOnline)
            foreach (var info in _certificateInfos.Values.Where(static value => value.Source == "acme"))
                await CheckCertificateStatusAsync(info, cancellationToken);
    }

    /// <summary>
    /// 为域名申请证书
    /// </summary>
    public async Task<X509Certificate2?> RequestCertificateAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (_acmeContexts.Count == 0 && !_options.InternalCaEnabled)
        {
            _logger.Error("ACME 上下文未初始化");
            return null;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var issuanceLock = await CertificateIssuanceLock.TryAcquireAsync(
                Path.GetFullPath(_options.CoordinationLockPath), domain,
                TimeSpan.FromSeconds(Math.Clamp(_options.CoordinationLockTimeoutSeconds, 1, 300)), cancellationToken);
            if (issuanceLock is null)
            {
                _logger.Warn("等待域名 {Domain} 的签发锁超时", domain);
                return GetCertificate(domain);
            }
            var shared = TryLoadSharedCertificate(domain);
            if (shared is not null && shared.NotAfter > DateTime.UtcNow.AddDays(_options.RenewBeforeDays))
                return shared;
            for (var index = 0; index < _acmeContexts.Count; index++)
                try
                {
                    return await RequestCertificateFromContextAsync(_acmeContexts[index], domain, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "ACME CA #{Index} 为 {Domain} 签发失败，尝试下一 CA", index + 1, domain);
                }
            _logger.Error("所有 ACME CA 均无法为域名 {Domain} 签发证书", domain);
            return _options.InternalCaEnabled ? await CreateInternalCertificateAsync(domain, cancellationToken) : null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<X509Certificate2?> RequestCertificateFromContextAsync(AcmeContext acmeContext, string domain,
        CancellationToken cancellationToken)
    {
            _logger.Info("开始为域名 {Domain} 申请证书", domain);

            // 创建订单
            var order = await acmeContext.NewOrder(new[] { domain });

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
            RestrictPrivateKey(keyPath);

            // 加载证书
            var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            _certificates[domain] = cert;
            _certificateInfos[domain] = new DomainCertificate
            {
                Domain = domain,
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                ExpiresAt = cert.NotAfter,
                Source = "acme",
                Status = "valid"
            };
            _metrics?.SetCertificateStatus(domain, cert.NotAfter, "acme", "valid");

            _logger.Info("域名 {Domain} 证书申请成功，过期时间: {ExpiresAt}", domain, cert.NotAfter);

            return cert;
    }

    private async Task<X509Certificate2> CreateInternalCertificateAsync(string domain, CancellationToken token)
    {
        var certificate = await _internalCa.GetOrCreateAsync(domain, token);
        _certificates.AddOrUpdate(domain, certificate, (_, previous) =>
        {
            if (!ReferenceEquals(previous, certificate)) previous.Dispose();
            return certificate;
        });
        _certificateInfos[domain] = new DomainCertificate
        {
            Domain = domain,
            CertificatePath = Path.Combine(_options.InternalCaPath, domain.Replace("*", "_wildcard_", StringComparison.Ordinal) + ".pem"),
            PrivateKeyPath = Path.Combine(_options.InternalCaPath, domain.Replace("*", "_wildcard_", StringComparison.Ordinal) + ".key"),
            ExpiresAt = certificate.NotAfter
            ,Source = "internal"
            ,Status = "valid"
        };
        _metrics?.SetCertificateStatus(domain, certificate.NotAfter, "internal", "valid");
        return certificate;
    }

    private X509Certificate2? TryLoadSharedCertificate(string domain)
    {
        var safeDomain = domain.Replace("*", "_wildcard_", StringComparison.Ordinal);
        foreach (var directory in new[] { _options.CertificatePath, _options.InternalCaPath })
        {
            var certificatePath = Path.Combine(directory, safeDomain + ".pem");
            var keyPath = Path.Combine(directory, safeDomain + ".key");
            if (!File.Exists(certificatePath) || !File.Exists(keyPath)) continue;
            try
            {
                var certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
                _certificates.AddOrUpdate(domain, certificate, (_, previous) =>
                {
                    if (!ReferenceEquals(previous, certificate)) previous.Dispose();
                    return certificate;
                });
                _certificateInfos[domain] = new DomainCertificate
                {
                    Domain = domain, CertificatePath = certificatePath, PrivateKeyPath = keyPath,
                    ExpiresAt = certificate.NotAfter,
                    Source = directory == _options.InternalCaPath ? "internal" : "acme",
                    Status = "valid"
                };
                _metrics?.SetCertificateStatus(domain, certificate.NotAfter, _certificateInfos[domain].Source, "valid");
                return certificate;
            }
            catch (Exception ex) { _logger.Warn(ex, "读取共享证书失败: {Domain}", domain); }
        }
        return null;
    }

    private async Task CheckCertificateStatusAsync(DomainCertificate info, CancellationToken token)
    {
        var status = "unknown";
        try
        {
            if (!_certificates.TryGetValue(info.Domain, out var certificate)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RevocationCheckTimeoutSeconds, 1, 60)));
            status = await Task.Run(() =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(
                    Math.Clamp(_options.RevocationCheckTimeoutSeconds, 1, 60));
                var valid = chain.Build(certificate);
                if (chain.ChainStatus.Any(item => item.Status.HasFlag(X509ChainStatusFlags.Revoked))) return "revoked";
                return valid ? "valid" : "unknown";
            }, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { status = "error"; }
        catch (Exception ex) { _logger.Warn(ex, "证书状态检查失败: {Domain}", info.Domain); status = "error"; }
        info.Status = status;
        info.StatusCheckedAt = DateTimeOffset.UtcNow;
        _metrics?.SetCertificateStatus(info.Domain, info.ExpiresAt, info.Source, status);
    }

    internal Task ScheduleRenewal()
    {
        lock (_onDemandGate)
        {
            if (_stopping) return Task.CompletedTask;
            if (_renewalWork.IsCompleted) _renewalWork = RunRenewalSafelyAsync();
            return _renewalWork;
        }
    }

    private async Task RunRenewalSafelyAsync()
    {
        try { await CheckAndRenewCertificatesAsync(_shutdown.Token); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { _logger.Error(ex, "ACME 定时续期检查失败"); }
    }

    private static void RestrictPrivateKey(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    #region 手动申请/续期

    private static readonly string AcmeSettingsFile = Path.Combine("certs", "acme-settings.json");

    /// <summary>
    /// 获取已保存的邮箱
    /// </summary>
    public string? GetSavedEmail()
    {
        try
        {
            if (!File.Exists(AcmeSettingsFile)) return _options.Email;
            var json = File.ReadAllText(AcmeSettingsFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("email", out var emailProp))
                return emailProp.GetString();
        }
        catch { }
        return string.IsNullOrEmpty(_options.Email) ? null : _options.Email;
    }

    /// <inheritdoc />
    public IReadOnlyList<CertificateStatusSnapshot> GetCertificateStatuses() => _certificateInfos.Values
        .OrderBy(static value => value.Domain, StringComparer.OrdinalIgnoreCase)
        .Select(static value => new CertificateStatusSnapshot(value.Domain, value.ExpiresAt,
            Math.Max(0, (int)Math.Floor((value.ExpiresAt - DateTime.UtcNow).TotalDays)), value.Source, value.Status,
            value.StatusCheckedAt)).ToArray();

    /// <inheritdoc />
    public IReadOnlyList<AcmeAccountSnapshot> GetAccountStatuses()
    {
        var directories = _options.UseStaging
            ? new[] { WellKnownServers.LetsEncryptStagingV2 }
            : AcmeOnDemandPolicy.GetDirectoryUris(_options);
        return directories.Select((directory, index) => new AcmeAccountSnapshot(directory.AbsoluteUri,
            File.Exists(AccountKeyPath(index)), GetSavedEmail())).ToArray();
    }

    /// <summary>
    /// 保存邮箱
    /// </summary>
    private void SaveEmail(string email)
    {
        try
        {
            var dir = Path.GetDirectoryName(AcmeSettingsFile);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(AcmeSettingsFile, JsonSerializer.Serialize(new { email }));
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "保存 ACME 邮箱失败");
        }
    }

    /// <summary>
    /// 按需初始化 ACME 上下文（不依赖 Enabled 配置）
    /// </summary>
    private async Task EnsureAcmeContextAsync(string email)
    {
        if (_acmeContexts.Count > 0) return;
        System.IO.Directory.CreateDirectory(_options.CertificatePath);
        var directories = _options.UseStaging
            ? new[] { WellKnownServers.LetsEncryptStagingV2 }
            : AcmeOnDemandPolicy.GetDirectoryUris(_options);
        for (var index = 0; index < directories.Count; index++)
            try { _acmeContexts.Add(await CreateContextAsync(directories[index], AccountKeyPath(index), email)); }
            catch (Exception ex) { _logger.Warn(ex, "初始化 ACME CA 失败: {Directory}", directories[index]); }
        if (_acmeContexts.Count == 0 && !_options.InternalCaEnabled)
            throw new InvalidOperationException("没有可用的 ACME CA 账户。");
    }

    /// <summary>
    /// 手动申请/续期证书
    /// </summary>
    public async Task<AcmeRequestResult> ManualRequestCertificateAsync(string domain, string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return new AcmeRequestResult { ErrorMessage = "域名不能为空" };
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return new AcmeRequestResult { ErrorMessage = "请填写有效的联系邮箱" };

        try
        {
            // 按需初始化 ACME 上下文
            await EnsureAcmeContextAsync(email);

            // 保存邮箱
            SaveEmail(email);

            // 确保证书目录存在
            System.IO.Directory.CreateDirectory(_options.CertificatePath);

            // 申请证书（复用已有逻辑）
            var cert = await RequestCertificateAsync(domain, cancellationToken);
            if (cert == null)
                return new AcmeRequestResult { ErrorMessage = "证书申请失败，请确保域名已解析到本服务器且 80 端口可访问" };

            return new AcmeRequestResult
            {
                Success = true,
                Domain = domain,
                ExpiresAt = cert.NotAfter.ToString("yyyy-MM-dd")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Error(ex, "手动申请证书失败: {Domain}", domain);
            return new AcmeRequestResult { ErrorMessage = $"证书申请失败: {ex.Message}" };
        }
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_onDemandGate) _stopping = true;
        _shutdown.Cancel();
        _renewalTimer?.Dispose();
        _semaphore.Dispose();
        _shutdown.Dispose();

        foreach (var cert in _certificates.Values)
        {
            cert.Dispose();
        }
        _certificates.Clear();
    }
}
