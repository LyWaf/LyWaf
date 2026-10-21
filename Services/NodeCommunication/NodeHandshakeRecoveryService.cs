using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeHandshakeRecoveryResult(bool Ready, string Code, string ObservationId, int ConfirmedCommands = 0);

/// <summary>Read-only outcome reconciliation; never retries prepare/commit or progresses a deployment.</summary>
public sealed class NodeHandshakeRecoveryService(NodeRegistrationObserver observer, IDistributionNodeClient client,
    IOptionsMonitor<NodeCommunicationOptions> options, INodeHandshakePeerProbe probe)
{
    public async Task<NodeHandshakeRecoveryResult> ReconcileAsync(string peer, string observationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var observation = observer.Get(peer);
        if (observation is null || observation.ObservationId != observationId || observation.State == "IdentityConflict")
            return new(false, "node_handshake_reconciliation_required", observationId);
        var config = options.CurrentValue;
        if (!config.Peers.TryGetValue(peer, out var admission) || admission.AdmissionState != "Active"
            || !NodeAdmissionPolicy.Allows(admission, "node.status")
            || !admission.AllowedOperations.Contains("node.status", StringComparer.Ordinal))
            return new(false, "node_handshake_admission_denied", observationId);
        try
        {
            if (!observer.BeginReconciliation(peer, observationId))
                return new(false, "node_handshake_observation_changed", observationId);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            var records = Pending(config, peer);
            if (records.Length > 100) return new(false, "node_handshake_reconciliation_limit", observationId);
            if (records.Length > 0 && !admission.AllowedOperations.Contains("configuration.result", StringComparer.Ordinal))
                return new(false, "node_handshake_admission_denied", observationId);
            var fingerprint = Digest(records);
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                var result = await client.QueryRecordedOutcomeAsync(record, budget.Token);
                if (!result.Ack || result.NodeId != peer || result.DeploymentId != record.Message.CorrelationId
                    || result.Stage != record.Message.Type["configuration.".Length..])
                    return new(false, "node_handshake_outcome_unconfirmed", observationId);
            }
            var status = await client.GetStatusAsync(peer, budget.Token);
            if (status.NodeId != peer || status.Role != observation.Handshake.Role
                || status.ConfigVersionId != observation.Handshake.Configuration!.AppliedVersionId
                || status.State is not ("Idle" or "Applied" or "RolledBack" or "Prepared" or "Expired"))
                return new(false, "node_handshake_revision_unconfirmed", observationId);
            string? preparedFingerprint = null;
            if (status.State == "Prepared")
            {
                var prepared = Prepared(config, peer, status.LastDeploymentId);
                if (prepared.Length != 1 || !admission.AllowedOperations.Contains("configuration.result", StringComparer.Ordinal))
                    return new(false, "node_handshake_outcome_unconfirmed", observationId);
                preparedFingerprint = Digest(prepared);
                var result = await client.QueryRecordedOutcomeAsync(prepared[0], budget.Token);
                if (!result.Ack || result.NodeId != peer || result.Stage != "prepare" || result.DeploymentId != status.LastDeploymentId)
                    return new(false, "node_handshake_outcome_unconfirmed", observationId);
            }
            budget.Token.ThrowIfCancellationRequested();
            if (Digest(Pending(config, peer)) != fingerprint)
                return new(false, "node_handshake_outbox_changed", observationId);
            var current = await probe.CaptureAsync(peer, budget.Token);
            NodeHandshakeValidation.Validate(current, DateTimeOffset.UtcNow);
            if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(observation.Handshake))
                return new(false, "node_handshake_metadata_changed", observationId);
            budget.Token.ThrowIfCancellationRequested();
            if (Digest(Pending(config, peer)) != fingerprint)
                return new(false, "node_handshake_outbox_changed", observationId);
            if (preparedFingerprint is not null && Digest(Prepared(config, peer, status.LastDeploymentId)) != preparedFingerprint)
                return new(false, "node_handshake_outbox_changed", observationId);
            var ready = observer.CompleteReconciliation(peer, observationId,
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { fingerprint, status }))));
            var confirmed = records.Length + (preparedFingerprint is not null && !records.Any(r =>
                r.Message.Type == "configuration.prepare" && r.Message.CorrelationId == status.LastDeploymentId) ? 1 : 0);
            return new(ready, ready ? "node_handshake_ready" : "node_handshake_observation_changed", observationId, confirmed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or OperationCanceledException or NotSupportedException)
        { return new(false, "node_handshake_reconciliation_unavailable", observationId); }
    }

    private NodeOutboxRecord[] Pending(NodeCommunicationOptions config, string peer) => client.ReadOutbox()
        .Where(r => !r.Archived && r.Result is null && r.Attempts > 0 && r.Message.ClusterId == config.ClusterId
            && r.Message.SenderNodeId == config.NodeId && r.Message.ReceiverNodeId == peer
            && r.Message.Type is "configuration.prepare" or "configuration.commit" or "configuration.rollback")
        .OrderBy(r => r.Message.MessageId, StringComparer.Ordinal).ToArray();

    private static string Digest(NodeOutboxRecord[] records)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records)));

    private NodeOutboxRecord[] Prepared(NodeCommunicationOptions config, string peer, string? deployment) => client.ReadOutbox()
        .Where(r => deployment is not null && !r.Archived && r.Attempts > 0 && r.Result?.Ack != false
            && r.Message.ClusterId == config.ClusterId && r.Message.SenderNodeId == config.NodeId
            && r.Message.ReceiverNodeId == peer && r.Message.Type == "configuration.prepare"
            && r.Message.CorrelationId == deployment)
        .OrderBy(r => r.Message.MessageId, StringComparer.Ordinal).ToArray();
}
