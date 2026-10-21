using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;

public sealed record CcExecutionQuery(CcPolicyDescriptor Policy, string DecisionId, DateTimeOffset? WindowStart = null);
public sealed record CcPeerReply(string RequestId, string RequestDigest, CcWindowEvidence Evidence,
    CcExecutionState Execution, string KeyId, string Signature,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReceivedAt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? ProcessingMilliseconds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ViaController = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ControllerBootId = null);
public sealed record CcExecutionBatch(CcExecutionQuery[] Queries);
public sealed record CcPeerBatchReply(CcPeerReply[] Items);

/// <summary>Read-only handler. The route must verify the node envelope before invoking this method.</summary>
public sealed class CcExecutionQueryService(CcDecisionService decisions,
    IOptionsMonitor<NodeCommunicationOptions> communication, IOptionsMonitor<RuntimeStateOptions> runtime,
    CcCohortState? cohort = null, IStatisticService? statistics = null)
{
    public const string Operation = "cc.execution.query";
    public const int MaxBatchItems = 64;
    private static readonly System.Diagnostics.ActivitySource Diagnostics = new("LyWaf.Cc.Query");

    internal static bool ValidQuery(CcExecutionQuery? query) => query?.Policy is { } policy
        && policy.PolicyKey is { Length: > 0 and <= 512 } && policy.PolicyGeneration is not null
        && policy.RuleId is { Length: > 0 and <= 128 } && policy.RuleRevision is { Length: 64 }
        && policy.ProtectionScope is { Length: > 0 and <= 262 } && policy.Subject is { Length: > 0 and <= 80 }
        && query.DecisionId is { Length: 64 } && query.DecisionId.All(char.IsAsciiHexDigit);

    public IResult HandleAuthenticated(NodeMessage message, HttpContext context)
    {
        using var activity = Diagnostics.StartActivity("cc.execution.query");
        if (message.Payload.ValueKind != JsonValueKind.Object || !message.Payload.TryGetProperty("queries", out _))
            return HandleSingle(message, context, out _);
        CcExecutionBatch? batch;
        try { batch = message.Payload.Deserialize<CcExecutionBatch>(NodeMessageAuthentication.JsonOptions); }
        catch (JsonException) { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
        if (batch?.Queries is not { Length: > 0 and <= MaxBatchItems } queries || queries.Any(q => q?.Policy?.PolicyGeneration is null)
            || queries.Select(q => q.Policy.StateKey).Distinct(StringComparer.Ordinal).Count() != queries.Length)
            return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
        var replies = new List<CcPeerReply>(queries.Length);
        foreach (var query in queries)
        {
            var result = HandleSingle(message with { Payload = JsonSerializer.SerializeToElement(query, NodeMessageAuthentication.JsonOptions) }, context, out var reply, batchReply: true);
            if (reply is null) return result;
            replies.Add(reply);
        }
        return NodeProtocolResponse.Success(context, new CcPeerBatchReply(replies.ToArray()));
    }

    private IResult HandleSingle(NodeMessage message, HttpContext context, out CcPeerReply? proof, bool batchReply = false)
    {
        proof = null;
        var config = communication.CurrentValue;
        var options = runtime.CurrentValue;
        if (!config.Enabled || !options.Enabled || config.NodeId != options.NodeId
            || message.ClusterId != config.ClusterId || message.ReceiverNodeId != config.NodeId || message.Type != Operation)
            return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
        if (!config.Peers.TryGetValue(message.SenderNodeId, out var peer) || !peer.Enabled
            || !NodeAdmissionPolicy.Allows(peer, Operation) || !peer.AllowedOperations.Contains(Operation, StringComparer.Ordinal))
            return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        CcExecutionQuery? query;
        try { query = message.Payload.Deserialize<CcExecutionQuery>(NodeMessageAuthentication.JsonOptions); }
        catch (JsonException) { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
        if (!ValidQuery(query)) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
        var policy = query!.Policy;
        // Query permission does not grant visibility into every protection group. Reuse the existing outbound allowlist.
        var disclosure = new RuntimeStateMutation(CcDecisionService.DecisionDomain, policy.StateKey,
            options.NodeId, 1, DateTimeOffset.UtcNow, null, false, null);
        if (RuntimeStateReplicationAuthorization.AuthorizeSend(options, message.SenderNodeId, disclosure) is not null)
            return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        var execution = decisions.InspectExecution(policy, query.DecisionId);
        if (cohort is null || statistics is null) return NodeProtocolResponse.Success(context, new { execution });
        var rule = statistics.GetAdvancedCcRules().FirstOrDefault(r => r.Id == policy.RuleId);
        if (rule is null) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
        var window = query.WindowStart ?? CcDecisionService.Window(DateTimeOffset.UtcNow, rule.Period);
        if (window != CcDecisionService.Window(DateTimeOffset.UtcNow, rule.Period))
            return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
        var keyId = context.Request.Headers["X-LyWaf-KeyId"].ToString();
        if (!peer.Keys.TryGetValue(keyId, out var secret)) return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        var reply = new CcPeerReply(message.MessageId, CcPeerQueryClient.Digest(query), cohort.Capture(policy, rule, window), execution, keyId, "");
        if (CcQueryTiming.Read(context) is { } timing)
            reply = reply with { ReceivedAt = timing.ReceivedAt, ProcessingMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(timing.Started).TotalMilliseconds };
        reply = reply with { Signature = CcPeerQueryClient.Sign(reply, secret) };
        proof = reply;
        return batchReply ? Results.Empty : NodeProtocolResponse.Success(context, reply);
    }
}
