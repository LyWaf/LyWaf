using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.WafRule;

/// <summary>限制规则包来源、落盘位置、下载耗时和受信发布密钥。</summary>
public sealed class RulePackageOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "rule-packages";
    public long MaxPackageBytes { get; set; } = 8 * 1024 * 1024;
    public int DownloadTimeoutSeconds { get; set; } = 15;
    public Dictionary<string, string> TrustedPublicKeys { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>签名规则包。Signature 是对固定格式清单和 RulesSha256 的 RSA-SHA256 签名。</summary>
public sealed class SignedRulePackage
{
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string MinHostVersion { get; set; } = "0.0.0";
    public string? MaxHostVersion { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string KeyId { get; set; } = "";
    public string RulesSha256 { get; set; } = "";
    public string Signature { get; set; } = "";
    public List<PackagedWafRule> Rules { get; set; } = [];
}

/// <summary>携带漏洞标识和命中解释的可发布 WAF 规则。</summary>
public sealed class PackagedWafRule
{
    public WafCustomRule Rule { get; set; } = new();
    public List<string> Cves { get; set; } = [];
    public string Explanation { get; set; } = "";
    public List<string> References { get; set; } = [];
}

public sealed record RulePackageInfo(string PackageId, string Version, DateTimeOffset PublishedAt,
    int RuleCount, string RulesSha256, string KeyId);
public sealed record RulePackageState(string? StableVersion, string? PreviousVersion,
    string? CanaryVersion, int CanaryPercentage);
public sealed record RuleFeedback(string PackageId, string Version, string RuleId, string Outcome,
    string Comment, DateTimeOffset CreatedAt);

public interface IRulePackageService
{
    Task<RulePackageInfo> InstallAsync(Stream source, CancellationToken cancellationToken = default);
    Task<RulePackageInfo> DownloadAndInstallAsync(Uri source, CancellationToken cancellationToken = default);
    IReadOnlyList<RulePackageInfo> List();
    RulePackageState GetState();
    void Activate(string version, int canaryPercentage = 0);
    void Rollback();
    IReadOnlyList<WafCustomRule> GetRules(HttpContext context);
    PackagedWafRule? Explain(string version, string ruleId);
    Task SubmitFeedbackAsync(RuleFeedback feedback, CancellationToken cancellationToken = default);
}

/// <summary>验签、原子保存并通过不可变内存快照提供稳定版或灰度版规则。</summary>
public sealed class RulePackageService : IRulePackageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly RulePackageOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly string _root;
    private readonly object _sync = new();
    private Dictionary<string, SignedRulePackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private RulePackageState _state = new(null, null, null, 0);

    public RulePackageService(IOptions<RulePackageOptions> options, IHttpClientFactory clients)
    {
        _options = options.Value;
        _clients = clients;
        _root = Path.GetFullPath(_options.Directory);
        Directory.CreateDirectory(_root);
        LoadVerifiedPackages();
    }

    public IReadOnlyList<RulePackageInfo> List()
    {
        lock (_sync) return _packages.Values.Select(ToInfo).OrderByDescending(x => x.PublishedAt).ToArray();
    }

    public RulePackageState GetState() { lock (_sync) return _state; }

    public async Task<RulePackageInfo> DownloadAndInstallAsync(Uri source, CancellationToken cancellationToken = default)
    {
        if (source.Scheme is not ("https" or "http")) throw new InvalidDataException("规则包地址仅支持 HTTP/HTTPS");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.DownloadTimeoutSeconds, 1, 300)));
        using var response = await _clients.CreateClient(nameof(RulePackageService))
            .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > _options.MaxPackageBytes)
            throw new InvalidDataException("远程规则包超过大小限制");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await InstallAsync(stream, timeout.Token);
    }

    public async Task<RulePackageInfo> InstallAsync(Stream source, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new InvalidOperationException("规则包功能未启用");
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > Math.Clamp(_options.MaxPackageBytes, 1024, 64L * 1024 * 1024))
                throw new InvalidDataException("规则包超过大小限制");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var package = JsonSerializer.Deserialize<SignedRulePackage>(memory.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("规则包 JSON 无效");
        Validate(package);
        var path = PackagePath(package.Version);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, memory.ToArray(), cancellationToken);
        File.Move(temp, path, true);
        lock (_sync) _packages = new(_packages, StringComparer.OrdinalIgnoreCase) { [package.Version] = package };
        return ToInfo(package);
    }

    public void Activate(string version, int canaryPercentage = 0)
    {
        lock (_sync)
        {
            if (!_packages.ContainsKey(version)) throw new KeyNotFoundException($"规则包版本不存在: {version}");
            if (canaryPercentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(canaryPercentage));
            _state = canaryPercentage is > 0 and < 100
                ? _state with { CanaryVersion = version, CanaryPercentage = canaryPercentage }
                : new RulePackageState(version, _state.StableVersion, null, 0);
            SaveState();
        }
    }

    public void Rollback()
    {
        lock (_sync)
        {
            if (_state.PreviousVersion == null || !_packages.ContainsKey(_state.PreviousVersion))
                throw new InvalidOperationException("没有可回滚的最近有效版本");
            _state = new RulePackageState(_state.PreviousVersion, _state.StableVersion, null, 0);
            SaveState();
        }
    }

    public IReadOnlyList<WafCustomRule> GetRules(HttpContext context)
    {
        lock (_sync)
        {
            var version = _state.StableVersion;
            if (_state.CanaryVersion != null && InCanary(context, _state.CanaryPercentage)) version = _state.CanaryVersion;
            if (version == null || !_packages.TryGetValue(version, out var package)) return [];
            return package.Rules.Select(x => { x.Rule.Source = WafRuleSource.System; return x.Rule; }).ToArray();
        }
    }

    public PackagedWafRule? Explain(string version, string ruleId)
    {
        lock (_sync) return _packages.TryGetValue(version, out var package)
            ? package.Rules.FirstOrDefault(x => x.Rule.Id.Equals(ruleId, StringComparison.Ordinal)) : null;
    }

    public async Task SubmitFeedbackAsync(RuleFeedback feedback, CancellationToken cancellationToken = default)
    {
        if (feedback.RuleId.Length > 128 || feedback.Comment.Length > 2000 || feedback.Outcome.Length > 64)
            throw new InvalidDataException("反馈字段超过长度限制");
        var line = JsonSerializer.Serialize(feedback, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(Path.Combine(_root, "feedback.jsonl"), line, cancellationToken);
    }

    public static string SigningPayload(SignedRulePackage package) => string.Join('\n',
        package.PackageId, package.Version, package.MinHostVersion, package.MaxHostVersion ?? "",
        package.PublishedAt.ToUniversalTime().ToString("O"), package.KeyId, package.RulesSha256.ToLowerInvariant());

    public static string ComputeRulesSha256(IReadOnlyList<PackagedWafRule> rules)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rules, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private void Validate(SignedRulePackage package)
    {
        if (string.IsNullOrWhiteSpace(package.PackageId) || string.IsNullOrWhiteSpace(package.Version))
            throw new InvalidDataException("规则包 ID 和版本不能为空");
        if (package.Rules.Count is 0 or > 10000) throw new InvalidDataException("规则数量无效");
        if (!ComputeRulesSha256(package.Rules).Equals(package.RulesSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("规则内容 SHA-256 不匹配");
        var host = typeof(RulePackageService).Assembly.GetName().Version ?? new Version(0, 0);
        if (!Version.TryParse(package.MinHostVersion, out var min) || host < min ||
            (package.MaxHostVersion != null && (!Version.TryParse(package.MaxHostVersion, out var max) || host > max)))
            throw new InvalidDataException($"规则包与当前宿主 {host} 不兼容");
        if (!_options.TrustedPublicKeys.TryGetValue(package.KeyId, out var pem))
            throw new InvalidDataException($"未信任规则包发布密钥: {package.KeyId}");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(SigningPayload(package)), Convert.FromBase64String(package.Signature),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("规则包签名验证失败");
        }
        catch (FormatException ex) { throw new InvalidDataException("规则包签名格式无效", ex); }
    }

    private void LoadVerifiedPackages()
    {
        var loaded = new Dictionary<string, SignedRulePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_root, "*.rules.json"))
        {
            try
            {
                if (new FileInfo(path).Length > _options.MaxPackageBytes) continue;
                var package = JsonSerializer.Deserialize<SignedRulePackage>(File.ReadAllBytes(path), JsonOptions);
                if (package == null) continue;
                Validate(package);
                loaded[package.Version] = package;
            }
            catch { /* 无效落盘包不进入运行快照。 */ }
        }
        var statePath = Path.Combine(_root, "state.json");
        var state = File.Exists(statePath) ? JsonSerializer.Deserialize<RulePackageState>(File.ReadAllText(statePath), JsonOptions) : null;
        _packages = loaded;
        _state = state ?? new(null, null, null, 0);
        if (_state.StableVersion != null && !loaded.ContainsKey(_state.StableVersion)) _state = new(null, null, null, 0);
    }

    private void SaveState()
    {
        var path = Path.Combine(_root, "state.json");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(temp, path, true);
    }

    private string PackagePath(string version)
    {
        if (version.Length is 0 or > 64 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException("规则包版本格式无效");
        return Path.Combine(_root, version + ".rules.json");
    }

    private static RulePackageInfo ToInfo(SignedRulePackage x) =>
        new(x.PackageId, x.Version, x.PublishedAt, x.Rules.Count, x.RulesSha256, x.KeyId);

    private static bool InCanary(HttpContext context, int percentage)
    {
        var identity = $"{context.Connection.RemoteIpAddress}|{context.Request.Host.Host}|{context.Request.Path}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return BitConverter.ToUInt32(hash, 0) % 100 < percentage;
    }
}
