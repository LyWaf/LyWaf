using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeRegistrationObservation(NodeHandshake Handshake, string ObservationId, string State,
    DateTimeOffset LastSeen, string ControllerSession, IReadOnlyList<string> RetiredBootIds,
    string? ConflictingBootId = null, DateTimeOffset? ReconciledAt = null, string? ReconciliationDigest = null,
    NodeHandshake? ConflictingHandshake = null, IReadOnlyList<NodeIdentityResolution>? IdentityResolutions = null,
    string? UpdateId = null);

internal sealed record NodeProbeObservation(string ClusterId, string NodeId, string StateDirectory,
    string Peer, string Path, string? ObservationId, string? UpdateId);

public sealed record NodeIdentityResolution(string RequestId, string ObservationId, string AcceptedBootId,
    string RetiredBootId, string Actor, string RemoteIp, string Reason, DateTimeOffset ConfirmedAt);

/// <summary>Controller-local durable observations. Remote metadata never approves a peer or opens the task gate.</summary>
public sealed class NodeRegistrationObserver(IOptionsMonitor<NodeCommunicationOptions> options, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _disconnected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _observedAfterDisconnect = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ObservationId, string? UpdateId)> _probeDisconnected = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    internal Action? BeforePersist { get; set; }
    public static TimeSpan ObservationLifetime => TimeSpan.FromSeconds(60);

    public NodeRegistrationObservation ObserveAuthenticated(NodeMessage envelope)
    {
        var config = Controller();
        if (envelope.Type != "node.handshake" || envelope.ClusterId != config.ClusterId || envelope.ReceiverNodeId != config.NodeId
            || envelope.ProtocolVersion != 2 || envelope.ExpiresAt <= _clock.GetUtcNow()
            || !Guid.TryParseExact(envelope.MessageId, "D", out _)) throw new InvalidDataException("Handshake envelope mismatch");
        var peer = Peer(config, envelope.SenderNodeId);
        var handshake = envelope.Payload.Deserialize<NodeHandshake>(NodeMessageAuthentication.JsonOptions)
            ?? throw new InvalidDataException("Missing handshake");
        NodeHandshakeValidation.Validate(handshake, _clock.GetUtcNow());
        if (handshake.ClusterId != envelope.ClusterId || handshake.NodeId != envelope.SenderNodeId || handshake.Role != peer.Role)
            throw new InvalidDataException("Handshake sender or role mismatch");
        lock (_gate)
        {
            var path = PathFor(config, handshake.NodeId);
            using var held = Hold(path);
            var old = Read(path, config, handshake.NodeId);
            var now = _clock.GetUtcNow();
            if (old?.RetiredBootIds.Contains(handshake.BootId, StringComparer.Ordinal) == true)
                throw new InvalidDataException("Retired node boot cannot register again");
            if (old?.State == "IdentityConflict") return old;
            if (handshake.Ending && (old is null || old.Handshake.BootId != handshake.BootId))
                throw new InvalidDataException("Only the current registered boot can end its session");
            if (old is not null && old.Handshake.Ending && old.Handshake.BootId == handshake.BootId && !handshake.Ending)
                throw new InvalidDataException("Ended node boot cannot register again");
            if (old is not null && old.Handshake.BootId != handshake.BootId
                && !old.Handshake.Ending && now - old.LastSeen < ObservationLifetime)
            {
                var conflict = old with { State = "IdentityConflict", ConflictingBootId = handshake.BootId,
                    ConflictingHandshake = handshake, ObservationId = Guid.NewGuid().ToString("N"), ReconciledAt = null, ReconciliationDigest = null };
                return Save(path, conflict);
            }
            if (old is not null && old.Handshake.BootId == handshake.BootId && old.Handshake.StartedAt != handshake.StartedAt)
                throw new InvalidDataException("One boot identity cannot have two start times");
            var retired = old?.RetiredBootIds.ToList() ?? [];
            if (old is not null && old.Handshake.BootId != handshake.BootId)
            {
                if (retired.Count >= 128) throw new InvalidOperationException("Boot history requires operator review");
                retired.Add(old.Handshake.BootId);
            }
            var unchanged = !_disconnected.Contains(handshake.NodeId) && !ProbeDisconnected(path, old)
                && old is not null && old.ControllerSession == _session && now - old.LastSeen < ObservationLifetime
                && old.Handshake.BootId == handshake.BootId && MetadataDigest(old.Handshake) == MetadataDigest(handshake);
            var next = new NodeRegistrationObservation(handshake, unchanged ? old!.ObservationId : Guid.NewGuid().ToString("N"),
                unchanged ? old!.State : "NeedsReconciliation", now, _session, retired,
                ReconciledAt: unchanged ? old!.ReconciledAt : null, ReconciliationDigest: unchanged ? old!.ReconciliationDigest : null,
                IdentityResolutions: old?.IdentityResolutions);
            next = Save(path, next);
            _probeDisconnected.Remove(path);
            if (_disconnected.Contains(handshake.NodeId)) _observedAfterDisconnect[handshake.NodeId] = next.ObservationId;
            return next;
        }
    }

    public NodeRegistrationObservation? Get(string peer)
    {
        var config = Controller();
        if (!config.Peers.ContainsKey(peer)) throw new UnauthorizedAccessException("Unknown node");
        lock (_gate) return Read(PathFor(config, peer), config, peer);
    }

    internal NodeProbeObservation CaptureProbe(string peer)
    {
        lock (_gate)
        {
            var config = Controller();
            Peer(config, peer);
            // Copy identity values; do not retain a mutable options object across network awaits.
            var identity = new NodeCommunicationOptions { ClusterId = config.ClusterId, NodeId = config.NodeId,
                StateDirectory = Path.GetFullPath(config.StateDirectory) };
            var path = PathFor(identity, peer);
            using var held = Hold(path);
            var value = Read(path, identity, peer);
            return new(identity.ClusterId, identity.NodeId, identity.StateDirectory, peer, path,
                value?.ObservationId, value?.UpdateId);
        }
    }

    internal bool MarkDisconnected(NodeProbeObservation expected)
    {
        // No starting observation means there is no authority to invalidate a later one.
        if (expected.ObservationId is null) return false;
        lock (_gate)
        {
            var config = Controller();
            if (config.ClusterId != expected.ClusterId || config.NodeId != expected.NodeId
                || Path.GetFullPath(config.StateDirectory) != expected.StateDirectory
                || !config.Peers.ContainsKey(expected.Peer)) return false;
            var identity = new NodeCommunicationOptions { ClusterId = expected.ClusterId, NodeId = expected.NodeId,
                StateDirectory = expected.StateDirectory };
            using var held = Hold(expected.Path);
            var value = Read(expected.Path, identity, expected.Peer);
            if (value is null || value.State == "IdentityConflict" || value.ObservationId != expected.ObservationId
                || value.UpdateId != expected.UpdateId) return false;
            // Fence this exact identity/version before fallible persistence, never another peer namespace.
            _probeDisconnected[expected.Path] = (value.ObservationId, value.UpdateId);
            var next = Save(expected.Path, value with { State = "NeedsReconciliation",
                ObservationId = Guid.NewGuid().ToString("N"), ReconciledAt = null, ReconciliationDigest = null });
            _probeDisconnected[expected.Path] = (next.ObservationId, next.UpdateId);
            return true;
        }
    }

    private bool ProbeDisconnected(string path, NodeRegistrationObservation? value) => value is not null
        && _probeDisconnected.TryGetValue(path, out var failed)
        && failed.ObservationId == value.ObservationId && failed.UpdateId == value.UpdateId;

    public bool CanStartTasks(string peer, string observationId)
    {
        var config = Controller();
        if (!config.Peers.TryGetValue(peer, out var admission) || !admission.Enabled || admission.AdmissionState != "Active") return false;
        lock (_gate)
        {
            if (_disconnected.Contains(peer)) return false;
            var path = PathFor(config, peer);
            var value = Read(path, config, peer);
            if (ProbeDisconnected(path, value)) return false;
            return value is { State: "Ready" } && value.ObservationId == observationId && value.ControllerSession == _session
                && !value.Handshake.Ending
                && value.Handshake.Role == admission.Role
                && _clock.GetUtcNow() - value.LastSeen < ObservationLifetime;
        }
    }

    public void MarkDisconnected(string peer)
    {
        lock (_gate)
        {
            // Invalidate memory before any fallible configuration or disk work. Old durable Ready is not authority.
            _disconnected.Add(peer);
            _observedAfterDisconnect.Remove(peer);
            var config = Controller();
            if (!config.Peers.ContainsKey(peer)) throw new UnauthorizedAccessException("Unknown node");
            var path = PathFor(config, peer);
            using var held = Hold(path);
            if (Read(path, config, peer) is { State: not "IdentityConflict" } value)
                Save(path, value with { State = "NeedsReconciliation", ObservationId = Guid.NewGuid().ToString("N"),
                    ReconciledAt = null, ReconciliationDigest = null });
        }
    }

    internal bool CompleteReconciliation(string peer, string observationId, string digest)
    {
        var config = Controller();
        if (!config.Peers.TryGetValue(peer, out var admission) || !admission.Enabled || admission.AdmissionState != "Active") return false;
        lock (_gate)
        {
            var path = PathFor(config, peer);
            using var held = Hold(path);
            var value = Read(path, config, peer);
            if (ProbeDisconnected(path, value)) return false;
            if (_disconnected.Contains(peer) && (!_observedAfterDisconnect.TryGetValue(peer, out var fresh)
                || fresh != observationId)) return false;
            if (value is null || value.State == "IdentityConflict" || value.ObservationId != observationId
                || value.Handshake.Ending
                || value.Handshake.Role != admission.Role
                || value.ControllerSession != _session || _clock.GetUtcNow() - value.LastSeen >= ObservationLifetime) return false;
            Save(path, value with { State = "Ready", ReconciledAt = _clock.GetUtcNow(), ReconciliationDigest = digest });
            _disconnected.Remove(peer);
            _observedAfterDisconnect.Remove(peer);
            _probeDisconnected.Remove(path);
            return true;
        }
    }

    internal bool BeginReconciliation(string peer, string observationId)
    {
        var config = Controller();
        lock (_gate)
        {
            var path = PathFor(config, peer);
            using var held = Hold(path);
            var value = Read(path, config, peer);
            if (ProbeDisconnected(path, value)) return false;
            if (_disconnected.Contains(peer) && (!_observedAfterDisconnect.TryGetValue(peer, out var fresh)
                || fresh != observationId)) return false;
            if (value is null || value.State == "IdentityConflict" || value.ObservationId != observationId
                || value.ControllerSession != _session || _clock.GetUtcNow() - value.LastSeen >= ObservationLifetime) return false;
            Save(path, value with { State = "NeedsReconciliation", ReconciledAt = null, ReconciliationDigest = null });
            return true;
        }
    }

    internal NodeRegistrationObservation ConfirmConflict(string peer, NodeIdentityConflictRequest request, string actor, string remoteIp)
    {
        var config = Controller();
        Peer(config, peer);
        lock (_gate)
        {
            var path = PathFor(config, peer);
            using var held = Hold(path);
            var value = Read(path, config, peer) ?? throw new InvalidOperationException("Node observation not found");
            var audit = value.IdentityResolutions?.ToList() ?? [];
            if (audit.FirstOrDefault(a => a.RequestId == request.RequestId) is { } previous)
            {
                if (previous.ObservationId != request.ObservationId || previous.AcceptedBootId != request.AcceptedBootId
                    || previous.RetiredBootId != request.RetiredBootId || previous.Actor != actor || previous.Reason != request.Reason)
                    throw new InvalidOperationException("Resolution request identity reused");
                return value;
            }
            if (value.State != "IdentityConflict" || value.ObservationId != request.ObservationId
                || value.ConflictingHandshake is null || value.ConflictingBootId != value.ConflictingHandshake.BootId)
                throw new InvalidOperationException("Identity conflict changed; review the current observation");
            var accepted = request.AcceptedBootId == value.Handshake.BootId ? value.Handshake
                : request.AcceptedBootId == value.ConflictingHandshake.BootId ? value.ConflictingHandshake
                : throw new InvalidOperationException("Accepted boot was not observed");
            var rejected = accepted.BootId == value.Handshake.BootId ? value.ConflictingHandshake.BootId : value.Handshake.BootId;
            if (request.RetiredBootId != rejected || accepted.BootId == rejected
                || value.RetiredBootIds.Contains(accepted.BootId, StringComparer.Ordinal))
                throw new InvalidOperationException("Invalid identity retirement");
            if (audit.Count >= 100 || value.RetiredBootIds.Count >= 128)
                throw new InvalidOperationException("Identity audit capacity requires operator maintenance");
            audit.Add(new(request.RequestId, request.ObservationId, accepted.BootId, rejected, actor, remoteIp,
                request.Reason, _clock.GetUtcNow()));
            // Persist the audit and retirement atomically. Administrator confirmation is not reconciliation.
            var next = value with { Handshake = accepted, ObservationId = Guid.NewGuid().ToString("N"),
                State = "NeedsReconciliation", ControllerSession = _session, LastSeen = DateTimeOffset.MinValue,
                RetiredBootIds = value.RetiredBootIds.Append(rejected).Distinct(StringComparer.Ordinal).ToArray(),
                ConflictingBootId = null, ConflictingHandshake = null, ReconciledAt = null, ReconciliationDigest = null,
                IdentityResolutions = audit };
            return Save(path, next);
        }
    }

    private NodeCommunicationOptions Controller()
    {
        var config = options.CurrentValue;
        if (!config.Enabled || config.Role != "Controller" || !NodeMessageAuthentication.ValidId(config.ClusterId)
            || !NodeMessageAuthentication.ValidId(config.NodeId)) throw new InvalidOperationException("Handshake observer requires a controller");
        return config;
    }

    internal static NodePeerOptions Peer(NodeCommunicationOptions config, string id)
        => config.Peers.TryGetValue(id, out var peer) && NodeAdmissionPolicy.Allows(peer, "node.handshake")
            && peer.AllowedOperations.Contains("node.handshake", StringComparer.Ordinal)
            ? peer : throw new UnauthorizedAccessException("Handshake peer is not authorized");

    private static string MetadataDigest(NodeHandshake value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static string PathFor(NodeCommunicationOptions config, string peer)
    {
        var directory = Path.Combine(Path.GetFullPath(config.StateDirectory), "registrations");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid registration directory");
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { config.ClusterId, config.NodeId, peer }))) + ".json");
    }

    private static FileStream Hold(string path) => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static NodeRegistrationObservation? Read(string path, NodeCommunicationOptions config, string peer)
    {
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid registration file");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 512 * 1024) throw new InvalidDataException("Oversized registration");
        var value = JsonSerializer.Deserialize<NodeRegistrationObservation>(input);
        if (value?.Handshake is null || value.Handshake.ClusterId != config.ClusterId || value.Handshake.NodeId != peer
            || !Guid.TryParseExact(value.ObservationId, "N", out _) || value.RetiredBootIds is not { Count: <= 128 }
            || value.UpdateId is not null && !Guid.TryParseExact(value.UpdateId, "N", out _)
            || value.IdentityResolutions?.Count > 100
            || value.State is not ("Ready" or "NeedsReconciliation" or "IdentityConflict"))
            throw new InvalidDataException("Invalid registration");
        return value;
    }

    private NodeRegistrationObservation Save(string path, NodeRegistrationObservation value)
    {
        // ObservationId survives successful heartbeats; UpdateId fences every durable refresh,
        // including equal clock ticks and a reconciliation completing during an older probe.
        value = value with { UpdateId = Guid.NewGuid().ToString("N") };
        BeforePersist?.Invoke();
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (bytes.Length > 512 * 1024) throw new IOException("Registration audit capacity exceeded");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, true);
            return value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
