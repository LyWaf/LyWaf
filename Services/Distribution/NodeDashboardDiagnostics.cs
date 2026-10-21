using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Telemetry;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Distribution;

public sealed record NodeQueueDiagnostics(int Entries, long Bytes, long Coalesced, long CapacityDropped,
    long OversizeDropped, long ClosedDropped, long ContentionBuffered, long AuthorizationRejected);
public sealed record NodeLocalDiagnostics(NodeCommunicationSnapshot Communication, NodeQueueDiagnostics? ReplicationQueue);
public sealed record NodePeerDiagnostics(string? BootId, DateTimeOffset? LastHandshakeAt, string? ReconciliationState,
    string? ExpectedSourceVersion, string? AcknowledgedAppliedVersion, string? ActualAppliedVersion, string? ActualFileVersion,
    int? PendingCommands, int? UnknownCommands, long? RetryQueries, double? OldestPendingMilliseconds,
    string? Error, DateTimeOffset CapturedAt);

/// <summary>Read-only projections. Snapshot errors never overwrite the node observation's error.</summary>
public sealed class NodeDashboardDiagnostics(
    RuntimeStateReplicator? replicator = null,
    IOptionsMonitor<NodeCommunicationOptions>? communication = null,
    NodeRegistrationObserver? registrations = null,
    DistributionControllerService? controller = null)
{
    public NodeLocalDiagnostics CaptureLocal()
    {
        var q = replicator?.QueueStatus;
        return new(NodeCommunicationTelemetry.Shared.Capture(), q is null ? null :
            new(q.Entries, q.Bytes, q.Coalesced, q.CapacityDropped, q.OversizeDropped,
                q.ClosedDropped, q.ContentionBuffered, replicator!.AuthorizationRejectedMutations));
    }

    public async Task<IReadOnlyDictionary<string, NodePeerDiagnostics>> CapturePeersAsync(
        DistributionOptions distribution, DateTimeOffset now, CancellationToken token)
    {
        IReadOnlyList<NodeOutboxRecord>? records = null;
        IReadOnlyList<ConfigurationDeployment> deployments = [];
        string? error = null;
        var config = communication?.CurrentValue;
        if (config is { Enabled: true })
        {
            try { records = new NodeCommandOutbox(config.StateDirectory).ReadRecords()
                .Where(r => r.Message.ClusterId == config.ClusterId && r.Message.SenderNodeId == config.NodeId).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            { error = "outbox_unavailable"; }
        }
        try { if (controller is not null) deployments = await controller.ListAsync(token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { error ??= "deployment_metadata_unavailable"; }
        var result = new Dictionary<string, NodePeerDiagnostics>(StringComparer.Ordinal);
        foreach (var pair in distribution.Nodes.Where(p => p.Value.Enabled))
        {
            NodeRegistrationObservation? observation = null;
            var peerError = error;
            if (config is { Enabled: true, Role: "Controller" } && registrations is not null)
            {
                try { observation = registrations.Get(pair.Key); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { peerError ??= "registration_unavailable"; }
            }
            var peerRecords = records?.Where(r => r.Message.ReceiverNodeId == pair.Key && r.Endpoint == pair.Value.Endpoint).ToArray();
            var pending = peerRecords?.Where(r => !r.Archived && r.State is "Pending" or "Unknown").ToArray();
            var latest = deployments.OrderByDescending(x => x.CreatedAt).FirstOrDefault(x => x.Targets.ContainsKey(pair.Key));
            result[pair.Key] = new(observation?.Handshake.BootId, observation?.LastSeen, observation?.State,
                latest?.VersionId, latest?.Targets[pair.Key].ConfigVersionId,
                observation?.Handshake.Configuration?.AppliedVersionId, observation?.Handshake.Configuration?.MainFileVersionId,
                pending?.Length, peerRecords?.Count(r => !r.Archived && r.State == "Unknown"),
                peerRecords?.Sum(r => (long)Math.Max(0, r.Attempts - 1)),
                pending is { Length: > 0 } ? Math.Max(0, (now - pending.Min(r => r.Message.CreatedAt)).TotalMilliseconds) : null,
                peerError, now);
        }
        return result;
    }
}
