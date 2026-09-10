using System.ComponentModel.DataAnnotations;
using System.Net;

namespace LyWaf.Control;

// =============== 请求模型 ===============

public class AddIpRequest
{
    [Required, StringLength(128), IpAddress(true)]
    public string IpOrCidr { get; set; } = "";
}

public class RemoveIpRequest
{
    [Required, StringLength(128), IpAddress(true)]
    public string IpOrCidr { get; set; } = "";
}

public class SaveIpGroupRequest : IValidatableObject
{
    [Required, StringLength(64)]
    public string Name { get; set; } = "";
    [StringLength(4096)]
    public string? Description { get; set; }
    [MaxLength(10000)]
    public List<string> Entries { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        var validator = new IpAddressAttribute(true);
        if (Entries != null)
            for (var i = 0; i < Entries.Count; i++)
                if (!string.IsNullOrWhiteSpace(Entries[i]) && !validator.IsValid(Entries[i]))
                    yield return new ValidationResult("Invalid IP or CIDR", [$"Entries[{i}]"]);
    }
}

public class SetIpGroupUsageRequest
{
    public bool WhitelistEnabled { get; set; }
    public bool BlacklistEnabled { get; set; }
}

public class BlockIpRequest : IValidatableObject
{
    [Required, StringLength(128)]
    public string Ip { get; set; } = "";
    [StringLength(4096)]
    public string? Reason { get; set; }
    public TimeSpan? Duration { get; set; }
    /// <summary>
    /// 限制类型: blocked(封禁), captcha(验证码), throttled(限速), log(日志记录)
    /// </summary>
    [RegularExpression("^(blocked|captcha|throttled|log)$")]
    public string? Type { get; set; }
    /// <summary>
    /// 限速速度（KB/s），仅 type=throttled 时有效
    /// </summary>
    [Range(0, int.MaxValue / 1024)]
    public int SpeedLimit { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IPAddress.TryParse(Ip?.Trim(), out _)) yield return new ValidationResult("Invalid IP address", [nameof(Ip)]);
        if (Duration is { } duration && (duration <= TimeSpan.Zero || duration.TotalSeconds > int.MaxValue))
            yield return new ValidationResult("Duration is outside the supported range", [nameof(Duration)]);
    }
}

public class UnblockIpRequest
{
    [Required, StringLength(128), IpAddress]
    public string Ip { get; set; } = "";
}

public class AddWafRuleRequest
{
    [Required, StringLength(32768)]
    public string Pattern { get; set; } = "";
}

public class RemoveWafRuleRequest
{
    [Required, StringLength(32768)]
    public string Pattern { get; set; } = "";
}

public class AddCcRuleRequest : IValidatableObject
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
    [Range(1, int.MaxValue)]
    public int? Period { get; set; }
    [Range(1, int.MaxValue)]
    public int? LimitNum { get; set; }
    public TimeSpan? FbTime { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FbTime is { } duration && (duration <= TimeSpan.Zero || duration.TotalSeconds > int.MaxValue))
            yield return new ValidationResult("封禁时长必须为正数且不超过支持的秒数范围", [nameof(FbTime)]);
    }
}

public class RemoveCcRuleRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
}

// =============== 高级 CC 规则请求模型 ===============

public class CcConditionRequest
{
    [Required, LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcMatchTarget))]
    public string Target { get; set; } = "UrlPath";
    [Required, LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcMatchOperator))]
    public string Operator { get; set; } = "Equal";
    [MaxLength(100)]
    public List<string>? Values { get; set; }
}

public class AddAdvancedCcRuleRequest
{
    [Required, StringLength(200)]
    public string Name { get; set; } = "";
    public bool? Enabled { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcRuleType))]
    public string? Type { get; set; }
    [MaxLength(100)]
    public List<CcConditionRequest>? Conditions { get; set; }
    [Range(1, int.MaxValue)]
    public int? Period { get; set; }
    [Range(1, int.MaxValue)]
    public int? Threshold { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcAction))]
    public string? Action { get; set; }
    [Range(1, int.MaxValue)]
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class UpdateAdvancedCcRuleRequest
{
    [Required, StringLength(128)]
    public string Id { get; set; } = "";
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcRuleType))]
    public string? Type { get; set; }
    [MaxLength(100)]
    public List<CcConditionRequest>? Conditions { get; set; }
    [Range(1, int.MaxValue)]
    public int? Period { get; set; }
    [Range(1, int.MaxValue)]
    public int? Threshold { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcAction))]
    public string? Action { get; set; }
    [Range(1, int.MaxValue)]
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class RemoveAdvancedCcRuleRequest
{
    [Required, StringLength(128)]
    public string RuleId { get; set; } = "";
}

public class ToggleAdvancedCcRuleRequest
{
    [Required, StringLength(128)]
    public string RuleId { get; set; } = "";
    public bool? Enabled { get; set; }
}

public class AddCountryRequest
{
    [Required, StringLength(128)]
    public string Country { get; set; } = "";
}

public class RemoveCountryRequest
{
    [Required, StringLength(128)]
    public string Country { get; set; } = "";
}

public class AddRegionRequest
{
    [Required, StringLength(128)]
    public string Region { get; set; } = "";
}

public class RemoveRegionRequest
{
    [Required, StringLength(128)]
    public string Region { get; set; } = "";
}

public class AddIpThrottleRequest
{
    [Required, StringLength(128), IpAddress]
    public string Ip { get; set; } = "";
    [Range(1, int.MaxValue / 1024)]
    public int LimitKbps { get; set; }
}

public class RemoveIpThrottleRequest
{
    [Required, StringLength(128), IpAddress]
    public string Ip { get; set; } = "";
}

public class AddPathThrottleRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
    [Range(1, int.MaxValue / 1024)]
    public int LimitKbps { get; set; }
}

public class RemovePathThrottleRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
}

public class SetGlobalThrottleRequest
{
    [Range(0, int.MaxValue / 1024)]
    public int LimitKbps { get; set; }
}

public class UpdateConnectionLimitRequest
{
    [Range(0, int.MaxValue)]
    public int? MaxConnectionsPerIp { get; set; }
    [Range(0, int.MaxValue)]
    public int? MaxConnectionsPerDestination { get; set; }
    [Range(0, int.MaxValue)]
    public int? MaxTotalConnections { get; set; }
    [Range(400, 599)]
    public int? RejectStatusCode { get; set; }
}

public class PathConnectionLimitRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
    [Range(1, int.MaxValue)]
    public int MaxConnections { get; set; }
}

public class RemovePathConnectionLimitRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
}

public class CreateABTestRequest : IValidatableObject
{
    [Required, StringLength(128)]
    public string TestId { get; set; } = "";
    [StringLength(256)]
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Mode { get; set; } // Random, CookieSticky, IpHash, UserIdHash
    public string? CookieName { get; set; }
    [Range(1, 36500)]
    public int? CookieExpireDays { get; set; }
    [Required, MinLength(2), MaxLength(1000)]
    public Dictionary<string, int> Variants { get; set; } = new(); // { "A": 70, "B": 30 }
    public Dictionary<string, string>? VariantTargets { get; set; } // { "A": "destination-a", "B": "destination-b" }
    public List<string>? MatchPaths { get; set; }
    public List<string>? ExcludePaths { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Mode != null && (!Enum.TryParse<LyWaf.Services.ABTest.ABTestMode>(Mode, true, out var mode) || !Enum.IsDefined(mode)))
            yield return new ValidationResult("Invalid AB mode", [nameof(Mode)]);
        if (Variants != null && (Variants.Values.Any(weight => weight < 0) ||
            Variants.Values.Sum(weight => (long)weight) is <= 0 or > int.MaxValue))
            yield return new ValidationResult("Variant weights are invalid or exceed the supported total", [nameof(Variants)]);
    }
}

public class ToggleABTestRequest
{
    public bool? Enabled { get; set; }
}

public class ToggleFeatureRequest
{
    public bool? Enabled { get; set; }
}

public class SaveConfigRequest
{
    [Required, StringLength(8 * 1024 * 1024)]
    public string Content { get; set; } = "";
    public bool Reload { get; set; }
}

/// <summary>从受限升级收件箱登记并校验候选程序包的请求。</summary>
public sealed class StageUpgradeRequest
{
    [Required, StringLength(255)]
    public string PackageName { get; set; } = "";
    [Required, StringLength(64)]
    public string Version { get; set; } = "";
    [Required, RegularExpression("^[a-fA-F0-9]{64}$")]
    public string Sha256 { get; set; } = "";
}

/// <summary>请求在当前实例排空后激活指定候选版本。</summary>
public sealed class ActivateUpgradeRequest
{
    [Required, StringLength(64)]
    public string Version { get; set; } = "";
}

public class ConvertConfigRequest
{
    [Required, StringLength(8 * 1024 * 1024)]
    public string Content { get; set; } = "";
}

public class ParamUpdateRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("isFirstServer")]
    public bool? IsFirstServer { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("localAddress")]
    public string? LocalAddress { get; set; }
}

// =============== IP 请求日志请求模型 ===============

public class IpLogRequest : IValidatableObject
{
    [Required, StringLength(128)]
    public string Ip { get; set; } = "";
    public TimeSpan? Duration { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IPAddress.TryParse(Ip?.Trim(), out _))
            yield return new ValidationResult("Invalid IP address", [nameof(Ip)]);
        if (Duration is { } duration && (duration <= TimeSpan.Zero || duration.TotalSeconds > int.MaxValue))
            yield return new ValidationResult("Duration is outside the supported range", [nameof(Duration)]);
    }
}

// =============== 负载均衡请求模型 ===============

public class UpdateClusterPolicyRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ClusterId { get; set; } = "";
    [Required, StringLength(128)]
    public string Policy { get; set; } = "";
}

public class AddDestinationRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ClusterId { get; set; } = "";
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string DestinationId { get; set; } = "";
    [Required, StringLength(8192), HttpEndpoint]
    public string Address { get; set; } = "";
    [MaxLength(1000)]
    public Dictionary<string, string>? Metadata { get; set; }
}

public class UpdateDestinationRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ClusterId { get; set; } = "";
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string DestinationId { get; set; } = "";
    [StringLength(8192), HttpEndpoint]
    public string? Address { get; set; }
    [MaxLength(1000)]
    public Dictionary<string, string>? Metadata { get; set; }
}

public class RemoveDestinationRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ClusterId { get; set; } = "";
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string DestinationId { get; set; } = "";
}

public class BatchRemoveDestinationsRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ClusterId { get; set; } = "";
    [Required, MinLength(1), MaxLength(100)]
    public List<string> DestinationIds { get; set; } = [];
}

public class RemoveClusterPatchRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ClusterId { get; set; } = "";
}

public class UpdateRouteRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string RouteId { get; set; } = "";
    [StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class UpdateRouteMatchRequest : IValidatableObject
{
    [StringLength(8192)]
    public string? Path { get; set; }
    [MaxLength(1000)]
    public List<string>? Hosts { get; set; }
    [MaxLength(100)]
    public List<string>? Methods { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Path != null)
        {
            bool valid;
            try { _ = Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(Path); valid = true; }
            catch (Microsoft.AspNetCore.Routing.Patterns.RoutePatternException) { valid = false; }
            if (!valid) yield return new ValidationResult("Invalid route pattern", [nameof(Path)]);
        }
        if (Methods != null)
            for (var i = 0; i < Methods.Count; i++)
                if (Methods[i] is not { Length: > 0 and <= 32 } method ||
                    !method.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
                    yield return new ValidationResult("Invalid HTTP method", [$"Methods[{i}]"]);
    }
}

public class AddRouteRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string RouteId { get; set; } = "";
    [StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class RemoveRoutePatchRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string RouteId { get; set; } = "";
}

public class AddFileServerRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ItemId { get; set; } = "";
    [StringLength(8192)]
    public string? Prefix { get; set; }
    [StringLength(4096)]
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    [Range(0, long.MaxValue)]
    public long? MaxFileSize { get; set; }
    [MaxLength(100)]
    public List<string>? TryFiles { get; set; }
    [MaxLength(100)]
    public List<string>? DefaultFiles { get; set; }
}

public class UpdateFileServerRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ItemId { get; set; } = "";
    [StringLength(8192)]
    public string? Prefix { get; set; }
    [StringLength(4096)]
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    [Range(0, long.MaxValue)]
    public long? MaxFileSize { get; set; }
    [MaxLength(100)]
    public List<string>? TryFiles { get; set; }
    [MaxLength(100)]
    public List<string>? DefaultFiles { get; set; }
}

public class RemoveFileServerRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ItemId { get; set; } = "";
}

public class AddSimpleResRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ItemId { get; set; } = "";
    [StringLength(1024 * 1024)]
    public string? Body { get; set; }
    [StringLength(256), RegularExpression(@"^[^\r\n]*$")]
    public string? ContentType { get; set; }
    [Range(100, 599)]
    public int? StatusCode { get; set; }
    [StringLength(64), RegularExpression(@"^[^\r\n]*$")]
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    [MaxLength(100), ResponseHeaders]
    public Dictionary<string, string>? Headers { get; set; }
}

public class UpdateSimpleResRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ItemId { get; set; } = "";
    [StringLength(1024 * 1024)]
    public string? Body { get; set; }
    [StringLength(256), RegularExpression(@"^[^\r\n]*$")]
    public string? ContentType { get; set; }
    [Range(100, 599)]
    public int? StatusCode { get; set; }
    [StringLength(64), RegularExpression(@"^[^\r\n]*$")]
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    [MaxLength(100), ResponseHeaders]
    public Dictionary<string, string>? Headers { get; set; }
}

public class RemoveSimpleResRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string ItemId { get; set; } = "";
}

public class PluginToggleRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string PluginId { get; set; } = "";
}

public class PluginConfigUpdateRequest
{
    [Required, StringLength(256), RegularExpression(@"^[^:\r\n]+$")]
    public string PluginId { get; set; } = "";
    [Required, MaxLength(1000)]
    public Dictionary<string, string> Config { get; set; } = new();
}

public class LoginRequest
{
    [StringLength(128)]
    public string? SecondFactor { get; set; }
    [Required, StringLength(256)]
    public string Username { get; set; } = "";
    /// <summary>SHA256(SHA256(password) + timestamp)</summary>
    [Required, RegularExpression("^[a-fA-F0-9]{64}$")]
    public string PasswordHash { get; set; } = "";
    /// <summary>客户端时间戳（Unix 秒，经服务器时间校准）</summary>
    public long Timestamp { get; set; }
}

public class ChangePasswordRequest
{
    [Required, StringLength(1024)]
    public string CurrentPassword { get; set; } = "";
    [Required, StringLength(1024, MinimumLength = 6)]
    public string NewPassword { get; set; } = "";
}

public class CertUploadRequest : IValidatableObject
{
    [Required, StringLength(253)]
    public string Domain { get; set; } = "";
    [Required, StringLength(262144)]
    public string PemContent { get; set; } = "";
    [Required, StringLength(65536)]
    public string KeyContent { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var host = Domain?.StartsWith("*.", StringComparison.Ordinal) == true ? Domain[2..] : Domain;
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            host.IndexOfAny(['/', '\\', ':']) >= 0)
            yield return new ValidationResult("请输入有效的域名或 IPv4 地址，不得包含路径或端口", [nameof(Domain)]);
    }
}

public class CertDeleteRequest : IValidatableObject
{
    [Required, StringLength(255)]
    public string PemFile { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PemFile == null || PemFile != Path.GetFileName(PemFile) || PemFile.IndexOfAny(['/', '\\', ':']) >= 0 ||
            PemFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !PemFile.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult("只能指定 PEM 证书文件名，不得包含路径", [nameof(PemFile)]);
    }
}

public class AcmeApplyRequest
{
    [Required, StringLength(253)]
    public string Domain { get; set; } = "";
    [Required, StringLength(254), EmailAddress]
    public string Email { get; set; } = "";
}

public class AcmeRenewRequest
{
    [Required, StringLength(253)]
    public string Domain { get; set; } = "";
    [StringLength(254)]
    public string Email { get; set; } = "";
}

public class PathRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
}

public class PcapToggleRequest
{
    [Required, StringLength(256)]
    public string PortKey { get; set; } = "";
    public bool Enabled { get; set; }
    [MaxLength(1000)]
    public List<string>? PcapHosts { get; set; }
}
