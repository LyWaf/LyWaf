namespace LyWaf.Control;

// =============== 请求模型 ===============

public class AddIpRequest
{
    public string IpOrCidr { get; set; } = "";
}

public class RemoveIpRequest
{
    public string IpOrCidr { get; set; } = "";
}

public class SaveIpGroupRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Entries { get; set; } = [];
}

public class SetIpGroupUsageRequest
{
    public bool WhitelistEnabled { get; set; }
    public bool BlacklistEnabled { get; set; }
}

public class BlockIpRequest
{
    public string Ip { get; set; } = "";
    public string? Reason { get; set; }
    public TimeSpan? Duration { get; set; }
    /// <summary>
    /// 限制类型: blocked(封禁), captcha(验证码), throttled(限速), log(日志记录)
    /// </summary>
    public string? Type { get; set; }
    /// <summary>
    /// 限速速度（KB/s），仅 type=throttled 时有效
    /// </summary>
    public int SpeedLimit { get; set; }
}

public class UnblockIpRequest
{
    public string Ip { get; set; } = "";
}

public class AddWafRuleRequest
{
    public string Pattern { get; set; } = "";
}

public class RemoveWafRuleRequest
{
    public string Pattern { get; set; } = "";
}

public class AddCcRuleRequest
{
    public string Path { get; set; } = "";
    public int? Period { get; set; }
    public int? LimitNum { get; set; }
    public TimeSpan? FbTime { get; set; }
}

public class RemoveCcRuleRequest
{
    public string Path { get; set; } = "";
}

// =============== 高级 CC 规则请求模型 ===============

public class CcConditionRequest
{
    public string Target { get; set; } = "UrlPath";
    public string Operator { get; set; } = "Equal";
    public List<string>? Values { get; set; }
}

public class AddAdvancedCcRuleRequest
{
    public string Name { get; set; } = "";
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public List<CcConditionRequest>? Conditions { get; set; }
    public int? Period { get; set; }
    public int? Threshold { get; set; }
    public string? Action { get; set; }
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class UpdateAdvancedCcRuleRequest
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public List<CcConditionRequest>? Conditions { get; set; }
    public int? Period { get; set; }
    public int? Threshold { get; set; }
    public string? Action { get; set; }
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class RemoveAdvancedCcRuleRequest
{
    public string RuleId { get; set; } = "";
}

public class ToggleAdvancedCcRuleRequest
{
    public string RuleId { get; set; } = "";
    public bool? Enabled { get; set; }
}

public class AddCountryRequest
{
    public string Country { get; set; } = "";
}

public class RemoveCountryRequest
{
    public string Country { get; set; } = "";
}

public class AddRegionRequest
{
    public string Region { get; set; } = "";
}

public class RemoveRegionRequest
{
    public string Region { get; set; } = "";
}

public class AddIpThrottleRequest
{
    public string Ip { get; set; } = "";
    public int LimitKbps { get; set; }
}

public class RemoveIpThrottleRequest
{
    public string Ip { get; set; } = "";
}

public class AddPathThrottleRequest
{
    public string Path { get; set; } = "";
    public int LimitKbps { get; set; }
}

public class RemovePathThrottleRequest
{
    public string Path { get; set; } = "";
}

public class SetGlobalThrottleRequest
{
    public int LimitKbps { get; set; }
}

public class UpdateConnectionLimitRequest
{
    public int? MaxConnectionsPerIp { get; set; }
    public int? MaxConnectionsPerDestination { get; set; }
    public int? MaxTotalConnections { get; set; }
    public int? RejectStatusCode { get; set; }
}

public class PathConnectionLimitRequest
{
    public string Path { get; set; } = "";
    public int MaxConnections { get; set; }
}

public class RemovePathConnectionLimitRequest
{
    public string Path { get; set; } = "";
}

public class CreateABTestRequest
{
    public string TestId { get; set; } = "";
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Mode { get; set; } // Random, CookieSticky, IpHash, UserIdHash
    public string? CookieName { get; set; }
    public int? CookieExpireDays { get; set; }
    public Dictionary<string, int> Variants { get; set; } = new(); // { "A": 70, "B": 30 }
    public Dictionary<string, string>? VariantTargets { get; set; } // { "A": "destination-a", "B": "destination-b" }
    public List<string>? MatchPaths { get; set; }
    public List<string>? ExcludePaths { get; set; }
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
    public string Content { get; set; } = "";
    public bool Reload { get; set; }
}

/// <summary>从受限升级收件箱登记并校验候选程序包的请求。</summary>
public sealed class StageUpgradeRequest
{
    public string PackageName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>请求在当前实例排空后激活指定候选版本。</summary>
public sealed class ActivateUpgradeRequest
{
    public string Version { get; set; } = "";
}

public class ConvertConfigRequest
{
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

public class IpLogRequest
{
    public string Ip { get; set; } = "";
    public TimeSpan? Duration { get; set; }
}

// =============== 负载均衡请求模型 ===============

public class UpdateClusterPolicyRequest
{
    public string ClusterId { get; set; } = "";
    public string Policy { get; set; } = "";
}

public class AddDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string Address { get; set; } = "";
    public Dictionary<string, string>? Metadata { get; set; }
}

public class UpdateDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string? Address { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class RemoveDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
}

public class BatchRemoveDestinationsRequest
{
    public string ClusterId { get; set; } = "";
    public List<string> DestinationIds { get; set; } = [];
}

public class RemoveClusterPatchRequest
{
    public string ClusterId { get; set; } = "";
}

public class UpdateRouteRequest
{
    public string RouteId { get; set; } = "";
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class UpdateRouteMatchRequest
{
    public string? Path { get; set; }
    public List<string>? Hosts { get; set; }
    public List<string>? Methods { get; set; }
}

public class AddRouteRequest
{
    public string RouteId { get; set; } = "";
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class RemoveRoutePatchRequest
{
    public string RouteId { get; set; } = "";
}

public class AddFileServerRequest
{
    public string ItemId { get; set; } = "";
    public string? Prefix { get; set; }
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? TryFiles { get; set; }
    public List<string>? DefaultFiles { get; set; }
}

public class UpdateFileServerRequest
{
    public string ItemId { get; set; } = "";
    public string? Prefix { get; set; }
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? TryFiles { get; set; }
    public List<string>? DefaultFiles { get; set; }
}

public class RemoveFileServerRequest
{
    public string ItemId { get; set; } = "";
}

public class AddSimpleResRequest
{
    public string ItemId { get; set; } = "";
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public int? StatusCode { get; set; }
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class UpdateSimpleResRequest
{
    public string ItemId { get; set; } = "";
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public int? StatusCode { get; set; }
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class RemoveSimpleResRequest
{
    public string ItemId { get; set; } = "";
}

public class PluginToggleRequest
{
    public string PluginId { get; set; } = "";
}

public class PluginConfigUpdateRequest
{
    public string PluginId { get; set; } = "";
    public Dictionary<string, string> Config { get; set; } = new();
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    /// <summary>SHA256(SHA256(password) + timestamp)</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>客户端时间戳（Unix 秒，经服务器时间校准）</summary>
    public long Timestamp { get; set; }
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public class CertUploadRequest
{
    public string Domain { get; set; } = "";
    public string PemContent { get; set; } = "";
    public string KeyContent { get; set; } = "";
}

public class CertDeleteRequest
{
    public string PemFile { get; set; } = "";
}

public class AcmeApplyRequest
{
    public string Domain { get; set; } = "";
    public string Email { get; set; } = "";
}

public class AcmeRenewRequest
{
    public string Domain { get; set; } = "";
    public string Email { get; set; } = "";
}

public class PathRequest
{
    public string Path { get; set; } = "";
}

public class PcapToggleRequest
{
    public string PortKey { get; set; } = "";
    public bool Enabled { get; set; }
    public List<string>? PcapHosts { get; set; }
}
