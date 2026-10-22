using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.Statistic;
using LyWaf.Services.Captcha;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

public sealed record CcCaptchaTarget(string RuleId, string Scope, string Ip, string PolicyKey, CcPolicyGeneration Expected);
public sealed record CcCaptchaReleaseRequest(CcCaptchaTarget Target, string ChallengeId, DateTimeOffset VerifiedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AdvancedCcRule? NodeRule = null);
public sealed record CcCaptchaReleaseResponse(RuntimeStateMutation Fence, bool IssuerApplied = true, bool AllNodesConfirmed = false);
public sealed record CcCaptchaReleaseConfirmation(string IssuerNodeId, bool IssuerSigned, bool LocalApplied,
    string PropagationStatus = "pending", bool AllNodesConfirmed = false);

public sealed class CcCaptchaReleaseIssuer(IOptionsMonitor<NodeCommunicationOptions> communication,
    IOptionsMonitor<RuntimeStateOptions> runtime, IStatisticService statistics, IRuntimeStateStore store,
    ICcCaptchaConsumption? consumption = null)
{
    public const string Operation = "cc.captcha.release";
    private readonly ConcurrentDictionary<string, RateWindow> _rates = new(StringComparer.Ordinal);

    public IResult Handle(NodeMessage message, HttpContext? context = null) => HandleAsync(message, context).GetAwaiter().GetResult();

    public async Task<IResult> HandleAsync(NodeMessage message, HttpContext? context = null)
    {
        context ??= new DefaultHttpContext();
        context.RequestAborted.ThrowIfCancellationRequested();
        IResult Error(int status, string code) => NodeProtocolResponse.Error(context, code, status);
        var config = communication.CurrentValue;
        var options = runtime.CurrentValue;
        if (!options.Enabled || options.NodeId != config.NodeId || message.ReceiverNodeId != config.NodeId
            || string.IsNullOrWhiteSpace(options.CcReleasePrivateKeyPem)) return Error(503, "cc_issuer_unavailable");
        if (!config.Enabled || message.ClusterId != config.ClusterId || message.Type != Operation
            || message.ExpiresAt <= DateTimeOffset.UtcNow || !config.Peers.TryGetValue(message.SenderNodeId, out var peer)
            || !NodeAdmissionPolicy.Allows(peer, Operation)
            || !PluginNodeRequests.Allows(peer, Operation)) return Error(403, "cc_release_forbidden");
        CcCaptchaReleaseRequest? request;
        try { request = message.Payload.Deserialize<CcCaptchaReleaseRequest>(NodeMessageAuthentication.JsonOptions); }
        catch (JsonException) { return Error(400, "cc_release_invalid"); }
        if (request?.Target is not { } target || target.Expected is null || target.RuleId is not { Length: > 0 and <= 128 }
            || target.Scope is not { Length: > 0 and <= 262 } || target.PolicyKey is not { Length: > 0 and <= 512 }
            || target.Ip is not { Length: > 0 and <= 64 } || request.ChallengeId is not { Length: 64 }
            || !request.ChallengeId.All(char.IsAsciiHexDigit) || target.Expected.Version < 0 || target.Expected.Version == long.MaxValue
            || (target.Expected.Version == 0 ? target.Expected.ReleaseId != "" : !Guid.TryParseExact(target.Expected.ReleaseId, "N", out _))
            || request.VerifiedAt < DateTimeOffset.UtcNow.AddMinutes(-2)
            || request.VerifiedAt > DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(config.AllowedClockSkewSeconds, 5, 300))
            || !IPAddress.TryParse(target.Ip, out var ip) || ip.ToString() != target.Ip)
            return Error(400, "cc_release_invalid");
        var scopedGrant = peer.CcCaptchaReleaseGrants.Any(g => g.RuleId == target.RuleId && g.Scope == target.Scope);
        var managed = config.Mode == "Controller" && config.Role == "Controller" && peer.Role == "Node"
            && NodeAdmissionPolicy.Allows(peer, Operation) && consumption is not null;
        if (!scopedGrant && !managed)
            return Error(403, "cc_release_scope_forbidden");
        if (request.NodeRule is not null && (!managed || target.Scope != "node:" + message.SenderNodeId
            || request.NodeRule.Scope != CcScope.Node || request.NodeRule.Id != target.RuleId
            || JsonSerializer.SerializeToUtf8Bytes(request.NodeRule, NodeMessageAuthentication.JsonOptions).Length > 64 * 1024))
            return Error(400, "cc_release_invalid");
        // A node may describe only its own private namespace. Shared scopes still require the controller's rule.
        var rule = request.NodeRule ?? statistics.GetAdvancedCcRule(target.RuleId);
        if (rule is not { Enabled: true, Action: CcAction.Captcha }
            || !CcRequestIdentity.ValidConfiguration(rule) || rule.Subject != CcSubjectKind.Ip
            || (rule.Scope == CcScope.Node ? target.Scope != "node:" + message.SenderNodeId
                : rule.Scope == CcScope.Cluster ? target.Scope != "cluster:shared"
                : !string.IsNullOrWhiteSpace(rule.ProtectionGroup) ? target.Scope != "group:" + rule.ProtectionGroup.Trim()
                : !(target.Scope.StartsWith("route:", StringComparison.Ordinal) || target.Scope.StartsWith("host:", StringComparison.Ordinal)))
            || CcRuleStateKey.Create(rule, target.Ip, target.Scope) != target.PolicyKey)
            return Error(409, "cc_release_policy_mismatch");
        var rate = _rates.GetOrAdd(message.SenderNodeId, _ => new());
        lock (rate)
        {
            if (rate.Start.AddMinutes(1) <= DateTimeOffset.UtcNow) { rate.Start = DateTimeOffset.UtcNow; rate.Count = 0; }
            if (++rate.Count > Math.Clamp(peer.CcCaptchaReleasesPerMinute, 1, 600)) return Error(429, "cc_release_rate_limited");
        }
        try
        {
            if ((!scopedGrant || request.NodeRule is not null) && !await consumption!.WasConsumedAsync(request.ChallengeId, message.SenderNodeId,
                target.Expected.StateKey(target.PolicyKey), context.RequestAborted))
                return Error(403, "cc_release_scope_forbidden");
            context.RequestAborted.ThrowIfCancellationRequested();
            var fence = store.IssueCcCaptchaRelease(target.PolicyKey, target.Expected, ReleaseId(message.SenderNodeId, request));
            return NodeProtocolResponse.Success(context, new CcCaptchaReleaseResponse(fence));
        }
        catch (CcCaptchaGenerationConflictException) { return Error(409, "cc_release_generation_conflict"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CaptchaStoreUnavailableException)
        { return Error(503, "cc_issuer_unavailable"); }
    }

    internal static string ReleaseId(string sender, CcCaptchaReleaseRequest request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { sender, request.Target, request.ChallengeId }, NodeMessageAuthentication.JsonOptions)).AsSpan(0, 16)).ToLowerInvariant();

    private sealed class RateWindow { public DateTimeOffset Start = DateTimeOffset.UtcNow; public int Count; }
}
