using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

public sealed record CcPropagationStatus(string Status, bool ReplicationEnabled, string[] ExpectedNodes,
    string[] AcknowledgedNodes, string[] PendingNodes, bool AllNodesConfirmed,
    string ConfirmationKind = "durable-receipt", string Membership = "current-configured-peers");

public sealed class CcPropagationTracker(IOptionsMonitor<RuntimeStateOptions> runtime,
    IOptionsMonitor<NodeCommunicationOptions> communication, RuntimeStateSyncSender sender)
{
    public CcPropagationStatus Read(Func<RuntimeStateMutation, bool> matches)
        => Capture()(matches);

    public Func<Func<RuntimeStateMutation, bool>, CcPropagationStatus> Capture()
    {
        var config = runtime.CurrentValue;
        var protocol = communication.CurrentValue;
        var expected = config.Peers.Keys.Where(id => id != config.NodeId).Order(StringComparer.Ordinal).ToArray();
        var receipts = new Dictionary<string, RuntimeStateMutation[]>(StringComparer.Ordinal);
        foreach (var id in expected)
        {
            var peer = config.Peers[id];
            if (!config.Enabled || !protocol.Enabled || protocol.NodeId != config.NodeId
                || !peer.Enabled || !peer.DurableSyncEnabled || peer.ProtocolVersion != 2) continue;
            try
            {
                receipts[id] = sender.ReadCcConfirmations(protocol.ClusterId, config.NodeId, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            { /* An unreadable receipt cannot establish confirmation. */ }
        }
        return matches =>
        {
            var confirmed = receipts.Where(pair => pair.Value.Any(m =>
            {
                try { return matches(m); }
                catch (System.Text.Json.JsonException) { return false; }
                catch (InvalidOperationException) { return false; }
            })).Select(pair => pair.Key).ToArray();
            var pending = expected.Except(confirmed, StringComparer.Ordinal).ToArray();
            var all = config.Enabled && protocol.Enabled && expected.Length > 0 && pending.Length == 0;
            return new(all ? "confirmed" : "pending", config.Enabled, expected, confirmed, pending, all);
        };
    }
}
