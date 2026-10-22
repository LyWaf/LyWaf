using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;

public sealed record CcExecutionRelayQuery(string TargetNodeId, CcExecutionQuery[] Queries);

/// <summary>Analysis-owned business relay; the host dispatches only its declared plugin request.</summary>
public sealed class CcExecutionRelayService(CcPeerQueryClient client, NodeIdentityService identity,
    IOptionsMonitor<NodeCommunicationOptions> communication, IOptionsMonitor<RuntimeStateOptions> runtime)
{
    public const string Operation = "cc.execution.relay";
    private readonly SemaphoreSlim slots = new(8, 8);

    public async Task<IResult> HandleAsync(NodeMessage message, HttpContext context)
    {
        var config = communication.CurrentValue;
        var state = runtime.CurrentValue;
        if (!config.Enabled || config.Mode != "Controller" || config.Role != "Controller" || !state.Enabled
            || state.NodeId != config.NodeId || message.Type != Operation || message.ClusterId != config.ClusterId
            || message.ReceiverNodeId != config.NodeId || message.ExpiresAt <= DateTimeOffset.UtcNow
            || !config.Peers.TryGetValue(message.SenderNodeId, out var sender) || sender.Role != "Node"
            || !PluginNodeRequests.Allows(sender, Operation))
            return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        CcExecutionRelayQuery? query;
        try { query = message.Payload.Deserialize<CcExecutionRelayQuery>(NodeMessageAuthentication.JsonOptions); }
        catch (JsonException) { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
        if (query?.Queries is not { Length: > 0 and <= CcExecutionQueryService.MaxBatchItems } queries
            || queries.Any(item => !CcExecutionQueryService.ValidQuery(item))
            || queries.Select(item => item.Policy.StateKey).Distinct(StringComparer.Ordinal).Count() != queries.Length)
            return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
        if (!NodeMessageAuthentication.ValidId(query.TargetNodeId) || query.TargetNodeId == message.SenderNodeId || !config.Peers.TryGetValue(query.TargetNodeId, out var target)
            || target.Role != "Node" || !NodeAdmissionPolicy.Allows(target, CcExecutionQueryService.Operation)
            || !PluginNodeRequests.Allows(target, CcExecutionQueryService.Operation))
            return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        foreach (var item in queries)
        {
            var disclosure = new RuntimeStateMutation(CcDecisionService.DecisionDomain, item.Policy.StateKey,
                state.NodeId, 1, DateTimeOffset.UtcNow, null, false, null);
            if (RuntimeStateReplicationAuthorization.AuthorizeSend(state, message.SenderNodeId, disclosure) is not null)
                return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        }
        var keyId = context.Request.Headers["X-LyWaf-KeyId"].ToString();
        if (!sender.Keys.TryGetValue(keyId, out var secret)) return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
        if (!await slots.WaitAsync(0, context.RequestAborted)) return NodeProtocolResponse.Error(context, "cc_query_busy", 429);
        try
        {
            var observed = await client.QueryAsync(query.TargetNodeId, queries, true, context.RequestAborted);
            var current = communication.CurrentValue;
            var currentState = runtime.CurrentValue;
            if (!current.Enabled || current.Mode != "Controller" || current.Role != "Controller"
                || current.NodeId != message.ReceiverNodeId || current.ClusterId != message.ClusterId || !currentState.Enabled
                || currentState.NodeId != current.NodeId || !current.Peers.TryGetValue(message.SenderNodeId, out var active)
                || active.Role != "Node" || !NodeAdmissionPolicy.Allows(active, Operation)
                || !PluginNodeRequests.Allows(active, Operation)
                || !active.Keys.TryGetValue(keyId, out var currentSecret) || currentSecret != secret
                || !current.Peers.TryGetValue(query.TargetNodeId, out var currentTarget) || currentTarget.Role != "Node"
                || !NodeAdmissionPolicy.Allows(currentTarget, CcExecutionQueryService.Operation)
                || !PluginNodeRequests.Allows(currentTarget, CcExecutionQueryService.Operation)
                || queries.Any(item => RuntimeStateReplicationAuthorization.AuthorizeSend(currentState, message.SenderNodeId,
                    new(CcDecisionService.DecisionDomain, item.Policy.StateKey, currentState.NodeId, 1,
                        DateTimeOffset.UtcNow, null, false, null)) is not null))
                return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
            var boot = identity.Capture().BootId;
            var replies = observed.Replies.Select(reply =>
            {
                // Child timing belongs to the controller-child hop. The requester must bound the whole relay interval.
                var endorsed = reply with { RequestId = message.MessageId, KeyId = keyId, Signature = "",
                    ViaController = config.NodeId, ControllerBootId = boot, ReceivedAt = null, ProcessingMilliseconds = null };
                return endorsed with { Signature = CcPeerQueryClient.Sign(endorsed, secret) };
            }).ToArray();
            return NodeProtocolResponse.Success(context, new CcPeerBatchReply(replies));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        { return NodeProtocolResponse.Error(context, NodeFailure.FromException(error).Code, 503); }
        finally { slots.Release(); }
    }
}
