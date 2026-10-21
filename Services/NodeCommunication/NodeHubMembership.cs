using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeHubAuthority(string ReleasePublicKeyPem, string ChallengeSecret, string ReleaseTrustDomain);

public sealed record NodeHubMembershipProof(string ClusterId, string ControllerNodeId, string ControllerBootId,
    string ReceiverNodeId, string RequestId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> KnownNodes, string KeyId, string Signature,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] NodeHubAuthority? Authority = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ExpectedCcNodes = null);

/// <summary>Historical source identities endorsed by the controller, not a claim of online status or complete coverage.</summary>
public sealed class NodeHubMembership(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private sealed record Snapshot(NodeHubMembershipProof Proof, HashSet<string> Sources, string KeyFingerprint);
    private Snapshot? current;

    public IReadOnlyList<string> KnownNodes(string cluster, string controller)
    {
        var value = Volatile.Read(ref current);
        return value is not null && value.Proof.ClusterId == cluster && value.Proof.ControllerNodeId == controller
            && value.Proof.ExpiresAt > time.GetUtcNow() ? value.Proof.KnownNodes : [];
    }

    public bool IsFresh(string cluster, string controller) => KnownNodes(cluster, controller).Count != 0;
    internal IReadOnlyList<string>? ExpectedCcNodes(NodeCommunicationOptions config)
    {
        var value = Volatile.Read(ref current);
        if (value is null || !config.Enabled || config.Mode != "Node" || config.Role != "Node"
            || value.Proof.ClusterId != config.ClusterId || value.Proof.ControllerNodeId != config.ControllerNodeId
            || value.Proof.ReceiverNodeId != config.NodeId || value.Proof.ExpiresAt <= time.GetUtcNow()
            || !config.Peers.TryGetValue(config.ControllerNodeId, out var peer) || peer.Role != "Controller"
            || !NodeAdmissionPolicy.Allows(peer, NodeHandshakeService.Operation)
            || !peer.AllowedOperations.Contains(NodeHandshakeService.Operation, StringComparer.Ordinal)
            || !peer.Keys.TryGetValue(value.Proof.KeyId, out var secret) || Fingerprint(secret) != value.KeyFingerprint) return null;
        return value.Proof.ExpectedCcNodes;
    }
    public IReadOnlyList<string>? ExpectedCcNodes(string cluster, string controller)
    {
        var value = Volatile.Read(ref current);
        return value is not null && value.Proof.ClusterId == cluster && value.Proof.ControllerNodeId == controller
            && value.Proof.ExpiresAt > time.GetUtcNow() ? value.Proof.ExpectedCcNodes : null;
    }

    internal static string[] ConfiguredCcNodes(NodeCommunicationOptions config) => config.Peers
        .Where(pair => pair.Value.Role == "Node" && pair.Value.Enabled && pair.Value.AdmissionState is "Active" or "Draining")
        .Select(pair => pair.Key).Append(config.NodeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal string? BootId(string cluster, string controller)
    {
        var value = Volatile.Read(ref current);
        return value is not null && value.Proof.ClusterId == cluster && value.Proof.ControllerNodeId == controller
            && value.Proof.ExpiresAt > time.GetUtcNow() ? value.Proof.ControllerBootId : null;
    }
    public bool IsKnown(string cluster, string controller, string source)
    {
        var value = Volatile.Read(ref current);
        return value is not null && value.Proof.ClusterId == cluster && value.Proof.ControllerNodeId == controller
            && value.Proof.ExpiresAt > time.GetUtcNow() && value.Sources.Contains(source);
    }
    public void Invalidate() => Volatile.Write(ref current, null);

    public void Accept(NodeCommunicationOptions config, NodeMessage request, NodeHandshake remote, NodeHubMembershipProof? proof)
    {
        Validate(config, request, remote, proof, time.GetUtcNow());
        // Never retain a mutable array supplied by a decoder or another caller.
        var copy = proof! with { KnownNodes = Array.AsReadOnly(proof!.KnownNodes.ToArray()),
            ExpectedCcNodes = proof.ExpectedCcNodes is null ? null : Array.AsReadOnly(proof.ExpectedCcNodes.ToArray()) };
        // Validation binds the immutable proof once; frequent CC reads only recheck the current key fingerprint.
        Volatile.Write(ref current, new Snapshot(copy, copy.KnownNodes.ToHashSet(StringComparer.Ordinal),
            Fingerprint(config.Peers[config.ControllerNodeId].Keys[proof.KeyId])));
    }

    public static NodeHubMembershipProof Create(NodeCommunicationOptions config, NodeMessage request, NodeHandshake local,
        string keyId, DateTimeOffset now, NodeHubAuthority? authority = null, bool includeCcMembership = false)
    {
        if (!config.Enabled || config.Mode != "Controller" || config.Role != "Controller"
            || request.Type != NodeHandshakeService.Operation || request.ClusterId != config.ClusterId
            || request.ReceiverNodeId != config.NodeId || local.NodeId != config.NodeId || local.ClusterId != config.ClusterId
            || local.Role != "Controller" || !config.Peers.TryGetValue(request.SenderNodeId, out var peer) || peer.Role != "Node"
            || !NodeAdmissionPolicy.Allows(peer, NodeHandshakeService.Operation)
            || !peer.Keys.TryGetValue(keyId, out var secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidDataException("Cannot endorse node membership");
        // Keep revoked IDs for the provenance of historical counters. Admission is checked independently on every RPC.
        var nodes = config.Peers.Where(pair => pair.Value.Role == "Node").Select(pair => pair.Key)
            .Append(config.NodeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (nodes.Length > 1025 || nodes.Any(id => !NodeMessageAuthentication.ValidId(id))
            || authority is not null && !NodeSetupCrypto.ValidAuthority(authority))
            throw new InvalidDataException("Invalid node membership");
        if (peer.AdmissionState != "Active"
            || !peer.AllowedOperations.Contains(LyWaf.Services.Captcha.AuthorityCaptchaStore.IssueOperation, StringComparer.Ordinal)
            || !peer.AllowedOperations.Contains(LyWaf.Services.Captcha.AuthorityCaptchaStore.TakeOperation, StringComparer.Ordinal)) authority = null;
        var proof = new NodeHubMembershipProof(config.ClusterId, config.NodeId, local.BootId, request.SenderNodeId,
            request.MessageId, now, now.AddMinutes(1), nodes, keyId, "", authority,
            includeCcMembership ? ConfiguredCcNodes(config) : null);
        return proof with { Signature = Sign(proof, secret) };
    }

    public static void Validate(NodeCommunicationOptions config, NodeMessage request, NodeHandshake remote,
        NodeHubMembershipProof? proof, DateTimeOffset now)
    {
        if (!config.Enabled || config.Mode != "Node" || config.Role != "Node" || proof is null
            || request.Type != NodeHandshakeService.Operation || request.SenderNodeId != config.NodeId
            || request.ClusterId != config.ClusterId || request.ReceiverNodeId != config.ControllerNodeId
            || proof.ClusterId != config.ClusterId || proof.ControllerNodeId != config.ControllerNodeId
            || proof.ReceiverNodeId != config.NodeId || proof.RequestId != request.MessageId
            || remote.ClusterId != config.ClusterId || remote.NodeId != config.ControllerNodeId || remote.Role != "Controller"
            || proof.ControllerBootId != remote.BootId || !Guid.TryParseExact(proof.ControllerBootId, "N", out _)
            || proof.IssuedAt > now.AddSeconds(Math.Clamp(config.AllowedClockSkewSeconds, 5, 300))
            || proof.ExpiresAt <= now || proof.ExpiresAt - proof.IssuedAt != TimeSpan.FromMinutes(1)
            || proof.Authority is not null && !NodeSetupCrypto.ValidAuthority(proof.Authority)
            || proof.KnownNodes is not { Count: > 0 and <= 1025 }
            || proof.KnownNodes.Any(id => !NodeMessageAuthentication.ValidId(id))
            || !proof.KnownNodes.SequenceEqual(proof.KnownNodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || !proof.KnownNodes.Contains(config.NodeId, StringComparer.Ordinal)
            || !proof.KnownNodes.Contains(config.ControllerNodeId, StringComparer.Ordinal)
            || proof.ExpectedCcNodes is { } ccNodes && (ccNodes.Count is < 1 or > 1025
                || !ccNodes.SequenceEqual(ccNodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || !ccNodes.Contains(config.ControllerNodeId, StringComparer.Ordinal)
                || ccNodes.Any(id => !proof.KnownNodes.Contains(id, StringComparer.Ordinal)))
            || !config.Peers.TryGetValue(config.ControllerNodeId, out var peer) || peer.Role != "Controller"
            || !NodeAdmissionPolicy.Allows(peer, NodeHandshakeService.Operation)
            || !NodeMessageAuthentication.ValidId(proof.KeyId)
            || !peer.Keys.TryGetValue(proof.KeyId, out var secret) || Encoding.UTF8.GetByteCount(secret) < 32
            || proof.Signature is not { Length: 64 } || !proof.Signature.All(char.IsAsciiHexDigit)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(proof.Signature), Convert.FromHexString(Sign(proof, secret))))
            throw new InvalidDataException("Invalid controller membership proof");
    }

    private static string Fingerprint(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Sign(NodeHubMembershipProof value, string secret) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret), JsonSerializer.SerializeToUtf8Bytes(value with { Signature = "" }, NodeMessageAuthentication.JsonOptions)));
}
