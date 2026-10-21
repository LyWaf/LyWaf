using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

public sealed partial class PluginNodeMessageService
{
    public const string RelayOperation = "plugin.relay";

    internal async Task<PluginNodeReceiveResult> HandleRelayAsync(NodeMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = communication.CurrentValue;
        if (!config.Enabled || config.Mode != "Controller" || config.Role != "Controller"
            || message.Type != RelayOperation || message.ClusterId != config.ClusterId || message.ReceiverNodeId != config.NodeId
            || message.ExpiresAt <= DateTimeOffset.UtcNow || !Guid.TryParseExact(message.MessageId, "D", out _)
            || !config.Peers.TryGetValue(message.SenderNodeId, out var sender) || sender.Role != "Node"
            || !NodeAdmissionPolicy.Allows(sender, RelayOperation) || !sender.AllowedOperations.Contains(RelayOperation, StringComparer.Ordinal))
            return Reject("node_plugin_forbidden", 403);
        PluginNodeRelayPayload? relay;
        try { relay = message.Payload.Deserialize<PluginNodeRelayPayload>(Json); }
        catch (JsonException) { return Reject("node_plugin_invalid_payload", 400); }
        if (relay?.Message is not { } payload || !ValidPayload(payload) || payload.OriginNodeId is not null
            || message.CorrelationId != payload.PluginId || !NodeMessageAuthentication.ValidId(relay.TargetNodeId))
            return Reject("node_plugin_invalid_payload", 400);
        var policy = options.CurrentValue;
        if (!policy.Grants.TryGetValue(payload.PluginId, out var grant)
            || !grant.RelayGroups.Contains(payload.TargetGroup, StringComparer.Ordinal)
            || !grant.AllowedSenders.Contains(message.SenderNodeId, StringComparer.Ordinal)
            || !policy.Groups.TryGetValue(payload.TargetGroup, out var members)
            || !members.Contains(relay.TargetNodeId, StringComparer.Ordinal)
            || !config.Peers.TryGetValue(relay.TargetNodeId, out var destination) || destination.Role != "Node"
            || relay.TargetNodeId == config.NodeId || relay.TargetNodeId == message.SenderNodeId)
            return Reject("node_plugin_forbidden", 403);
        var denial = Authorize(payload, send: true, null, out _);
        if (denial is not null) return new(null, denial);
        if (!TakeRate(message.SenderNodeId, payload.PluginId, "relay")) return Reject("node_plugin_rate_limited", 429);
        try
        {
            var target = Target(relay.TargetNodeId, config);
            // The authenticated first hop supplies the origin. A caller cannot choose an origin or request another relay hop.
            var forwarded = payload with { OriginNodeId = message.SenderNodeId };
            var id = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new[] { "relay", message.SenderNodeId, message.MessageId })).AsSpan(0, 16)).ToString("D");
            var delivery = message with { Type = Operation, SenderNodeId = config.NodeId, ReceiverNodeId = target.NodeId,
                MessageId = id, Payload = JsonSerializer.SerializeToElement(forwarded, Json) };
            var result = await SendOneAsync(delivery, forwarded, target, config.StateDirectory, token);
            return new(new(payload.PluginId, payload.MessageType, payload.SchemaVersion, message.MessageId,
                target.NodeId, result.Ack, result.ErrorCode), null);
        }
        catch (InvalidOperationException) { return Reject("node_plugin_forbidden", 403); }
    }
}
