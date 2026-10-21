using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeReconciliationRequest(string RequestId, string Digest, string Reason, string? Assessment = null);
public sealed record NodeReconciliationIdentity(string ClusterId, string SenderNodeId, string ReceiverNodeId,
    string MessageId, string DeploymentId, string Stage, string Digest, string Endpoint, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record NodeReconciliationObservation(bool Ack, string? ConfigVersionId);
public sealed record NodeReconciliationAction(string RequestId, string Kind, string Actor, string RemoteIp,
    string Reason, string? Assessment, DateTimeOffset RequestedAt, string State = "Requested",
    DateTimeOffset? FinishedAt = null, NodeReconciliationObservation? ObservedNodeResult = null,
    int? StatusCode = null, string? ErrorCode = null, string? RemoteTraceId = null);
public sealed record NodeReconciliationJournal(NodeReconciliationIdentity Identity, IReadOnlyList<NodeReconciliationAction> Actions);
public sealed record NodeReconciliationView(NodeReconciliationIdentity Identity, string OutboxState, int Attempts,
    NodeReconciliationObservation? OutboxResult, int? StatusCode, string? ErrorCode, string? RemoteTraceId,
    bool RequiresReview, IReadOnlyList<NodeReconciliationAction> Actions);
public sealed class NodeReconciliationAccessDeniedException() : UnauthorizedAccessException("Target node access denied");

/// <summary>Operator evidence, never a replacement for a node ACK or a deployment transition.</summary>
public sealed class NodeReconciliationService(
    IDistributionNodeClient client,
    MultiNodeDashboardService permissions,
    IOptionsMonitor<DistributionOptions> distribution,
    IOptionsMonitor<NodeCommunicationOptions> communication)
{
    internal const int MaximumQueries = 10;
    internal const int MaximumActions = 100;

    public IReadOnlyList<NodeReconciliationView> List(string actor, string peer, int offset = 0, int limit = 50)
    {
        Authorize(actor, peer);
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid page bounds");
        var config = communication.CurrentValue;
        return client.ReadOutbox().Where(r => r.Message.ClusterId == config.ClusterId && r.Message.SenderNodeId == config.NodeId
                && r.Message.ReceiverNodeId == peer && IsConfiguration(r))
            .OrderByDescending(r => r.Message.CreatedAt).ThenBy(r => r.Message.MessageId, StringComparer.Ordinal)
            .Skip(offset).Take(limit).Select(r => View(r, [])).ToArray();
    }

    public NodeReconciliationView Get(string actor, string peer, string messageId)
    {
        var record = Find(actor, peer, messageId);
        var identity = Identity(record);
        var directory = DirectoryPath;
        using var gate = CommandLock(directory, identity);
        return View(record, Read(directory, identity).Actions);
    }

    public async Task<NodeReconciliationAction> QueryAsync(string actor, string ip, string peer, string messageId,
        NodeReconciliationRequest request, CancellationToken token)
        => await ActAsync(actor, ip, peer, messageId, request, query: true, token);

    public async Task<NodeReconciliationAction> AssessAsync(string actor, string ip, string peer, string messageId,
        NodeReconciliationRequest request, CancellationToken token)
        => await ActAsync(actor, ip, peer, messageId, request, query: false, token);

    private async Task<NodeReconciliationAction> ActAsync(string actor, string ip, string peer, string messageId,
        NodeReconciliationRequest request, bool query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var record = Find(actor, peer, messageId);
        Validate(request, query);
        if (ip.Length > 64 || ip.Any(char.IsControl)) throw new ArgumentException("Invalid remote address");
        var identity = Identity(record);
        if (identity.Digest != request.Digest) throw new InvalidOperationException("Original command digest changed");
        var directory = DirectoryPath;
        using var gate = CommandLock(directory, identity);
        var journal = Read(directory, identity);
        var kind = query ? "Query" : "Assessment";
        var previous = journal.Actions.SingleOrDefault(action => action.RequestId == request.RequestId);
        if (previous is not null)
        {
            if (previous.Kind != kind || previous.Actor != actor || previous.Reason != request.Reason || previous.Assessment != request.Assessment)
                throw new InvalidOperationException("Request identity reused with different content");
            return previous;
        }
        if (!Eligible(record)) throw new InvalidOperationException("Only stopped, expired or exhausted unresolved commands can be reviewed");
        if (journal.Actions.Count >= MaximumActions) throw new InvalidOperationException("Manual evidence capacity reached");
        var now = DateTimeOffset.UtcNow;
        if (query && (journal.Actions.Count(a => a.Kind == "Query") >= MaximumQueries
            || journal.Actions.Any(a => a.Kind == "Query" && a.RequestedAt > now.AddMinutes(-1))))
            throw new InvalidOperationException("Manual query budget or cooldown reached");
        var action = new NodeReconciliationAction(request.RequestId, kind, actor, ip, request.Reason, request.Assessment, now,
            query ? "Requested" : "Recorded", query ? null : now);
        journal = journal with { Actions = journal.Actions.Append(action).ToArray() };
        token.ThrowIfCancellationRequested();
        Save(directory, journal); // Failure here must prevent all network activity.
        if (!query) return action;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await client.QueryRecordedOutcomeAsync(record, token);
            if (result.NodeId != peer || result.DeploymentId != identity.DeploymentId || result.Stage != identity.Stage)
                throw new InvalidDataException("Query result identity mismatch");
            if (result.ConfigVersionId is { } version && (version.Length > 256 || version.Any(char.IsControl)))
                throw new InvalidDataException("Query result revision invalid");
            action = action with { State = "Observed", FinishedAt = DateTimeOffset.UtcNow, ObservedNodeResult = Observation(result) };
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException
            or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            var failure = NodeFailure.FromException(error);
            action = action with
            {
                State = error is OperationCanceledException && token.IsCancellationRequested ? "Cancelled" : "Unconfirmed",
                FinishedAt = DateTimeOffset.UtcNow, ErrorCode = failure.Code, RemoteTraceId = failure.RemoteTraceId,
                StatusCode = error is HttpRequestException http ? (int?)http.StatusCode : null
            };
        }
        // Complete the audit even on caller cancellation. A failed write leaves Requested, never a resend on restart.
        Save(directory, journal with { Actions = journal.Actions.Select(a => a.RequestId == action.RequestId ? action : a).ToArray() });
        token.ThrowIfCancellationRequested();
        return action;
    }

    private void Authorize(string actor, string peer)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || actor.Any(char.IsControl)
            || !permissions.CanOperate(actor, [peer])) throw new NodeReconciliationAccessDeniedException();
        if (distribution.CurrentValue is not { Enabled: true, Role: "Controller" }
            || communication.CurrentValue is not { Enabled: true, Role: "Controller" })
            throw new InvalidOperationException("Reconciliation requires an enabled v2 controller");
    }

    private NodeOutboxRecord Find(string actor, string peer, string messageId)
    {
        Authorize(actor, peer);
        if (!Guid.TryParseExact(messageId, "D", out _)) throw new ArgumentException("Invalid message identity");
        var config = communication.CurrentValue;
        return client.ReadOutbox().SingleOrDefault(r => r.Message.ClusterId == config.ClusterId
            && r.Message.SenderNodeId == config.NodeId && r.Message.ReceiverNodeId == peer
            && r.Message.MessageId == messageId && IsConfiguration(r)) ?? throw new KeyNotFoundException("Command not found");
    }

    private static bool IsConfiguration(NodeOutboxRecord r) => !r.Archived && r.Message.Type is "configuration.prepare" or "configuration.commit" or "configuration.rollback";
    private static bool Eligible(NodeOutboxRecord r) => !r.Archived && r.Result is null && r.Attempts > 0
        && (r.State == "Stopped" || r.State == "Unknown" && (r.Message.ExpiresAt <= DateTimeOffset.UtcNow || r.Attempts >= NodeCommandOutbox.MaximumAttempts));
    private static NodeReconciliationObservation Observation(NodeAcknowledgement result) => new(result.Ack,
        result.ConfigVersionId is { Length: <= 256 } version && !version.Any(char.IsControl) ? version : null);
    private static NodeReconciliationIdentity Identity(NodeOutboxRecord r) => new(r.Message.ClusterId, r.Message.SenderNodeId,
        r.Message.ReceiverNodeId, r.Message.MessageId, r.Message.CorrelationId, r.Message.Type["configuration.".Length..],
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { r.Message.Type, r.Message.CorrelationId, r.Message.Payload }))), r.Endpoint, r.Message.CreatedAt, r.Message.ExpiresAt);
    private static NodeReconciliationView View(NodeOutboxRecord r, IReadOnlyList<NodeReconciliationAction> actions) => new(Identity(r),
        r.State, r.Attempts, r.Result is null ? null : Observation(r.Result), r.StatusCode, r.ErrorCode, r.RemoteTraceId, Eligible(r), actions);

    private static void Validate(NodeReconciliationRequest request, bool query)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out var requestId) || requestId.ToString("D") != request.RequestId
            || request.Digest is not { Length: 64 } || !request.Digest.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 512 || request.Reason.Any(char.IsControl)
            || (query ? request.Assessment is not null : request.Assessment is not
                ("Unresolved" or "OperatorBelievesApplied" or "OperatorBelievesNotApplied" or "NeedsIntervention")))
            throw new ArgumentException("Invalid reconciliation request");
    }

    private string DirectoryPath => Path.Combine(Path.GetFullPath(communication.CurrentValue.StateDirectory), "reconciliation");
    private static string Key(NodeReconciliationIdentity identity) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new[] { identity.ClusterId, identity.SenderNodeId, identity.ReceiverNodeId, identity.MessageId })));
    private static FileStream CommandLock(string directory, NodeReconciliationIdentity identity)
    {
        Directory.CreateDirectory(directory);
        return new(Path.Combine(directory, Key(identity) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static NodeReconciliationJournal Read(string directory, NodeReconciliationIdentity identity)
    {
        var path = Path.Combine(directory, Key(identity) + ".json");
        if (!File.Exists(path)) return new(identity, []);
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Reconciliation journal too large");
        var journal = JsonSerializer.Deserialize<NodeReconciliationJournal>(stream);
        if (journal is null || journal.Identity != identity || journal.Actions is null || journal.Actions.Count > MaximumActions
            || journal.Actions.Any(a => a is null || !Guid.TryParseExact(a.RequestId, "D", out _)
                || string.IsNullOrWhiteSpace(a.Actor) || a.Actor.Length > 128
                || string.IsNullOrWhiteSpace(a.Reason) || a.Reason.Length > 512
                || a.Kind is not ("Query" or "Assessment")
                || (a.Kind == "Query" ? a.Assessment is not null || a.State is not ("Requested" or "Observed" or "Cancelled" or "Unconfirmed")
                    : a.State != "Recorded" || a.ObservedNodeResult is not null || a.Assessment is not
                        ("Unresolved" or "OperatorBelievesApplied" or "OperatorBelievesNotApplied" or "NeedsIntervention")))
            || journal.Actions.Count(a => a.Kind == "Query") > MaximumQueries
            || journal.Actions.Select(a => a.RequestId).Distinct().Count() != journal.Actions.Count)
            throw new InvalidDataException("Invalid reconciliation journal");
        return journal;
    }
    private static void Save(string directory, NodeReconciliationJournal journal)
    {
        using var quota = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(directory, Key(journal.Identity) + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > 1024 * 1024 || (!File.Exists(path) && Directory.EnumerateFiles(directory, "*.json").Take(10000).Count() >= 10000)
            || new DirectoryInfo(directory).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 64 * 1024 * 1024)
            throw new IOException("Reconciliation capacity reached");
        try
        {
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(path + ".tmp", path, true);
        }
        finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
    }
}
