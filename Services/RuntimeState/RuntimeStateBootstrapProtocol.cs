using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed record RuntimeStateBootstrapRequest(string Phase, string RequestId, string ScopeDigest,
    string? ImageDigest = null, int? PageIndex = null);
public sealed record RuntimeStateBootstrapScope(string[] Domains, string[] IncludedPrefixes,
    string[] AllGroupPrefixes, bool Ungrouped)
{
    public bool Contains(RuntimeStateMutation mutation)
    {
        if (!Domains.Contains(mutation.Domain, StringComparer.Ordinal)) return false;
        var key = mutation.Key;
        if (key.StartsWith("ccg:", StringComparison.Ordinal) && !CcPolicyGeneration.TryParseKey(key, out key, out _)) return false;
        return IncludedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))
            || Ungrouped && !AllGroupPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));
    }
    internal static RuntimeStateBootstrapScope From(RuntimeStateOptions options, RuntimeStateReplicationPolicy policy) => new(
        policy.AllowedDomains.Order(StringComparer.Ordinal).ToArray(),
        policy.AllowedProtectionGroups.SelectMany(g => options.ProtectionGroupKeyPrefixes[g]).Order(StringComparer.Ordinal).ToArray(),
        options.ProtectionGroupKeyPrefixes.Values.SelectMany(x => x).Order(StringComparer.Ordinal).ToArray(), policy.AllowUngroupedKeys);
    internal string Digest() => RuntimeStateBootstrapImage.Hash(this);
}

public sealed record RuntimeStateBootstrapImage(string ClusterId, string AuthorityNodeId, string RecipientNodeId,
    string RequestId, string OldEpoch, string NewEpoch, RuntimeStateBootstrapScope Scope,
    RuntimeStateMutation[] Items, DateTimeOffset CapturedAt, string Digest)
{
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, RuntimeStateSyncProtocol.Json)));
    internal string ComputeDigest() => Hash(new
    { ClusterId, AuthorityNodeId, RecipientNodeId, RequestId, OldEpoch, NewEpoch, Scope, Items, CapturedAt });
    internal void Validate()
    {
        if (!RuntimeStateSyncProtocol.ValidIdentity(ClusterId) || !RuntimeStateSyncProtocol.ValidIdentity(AuthorityNodeId)
            || !RuntimeStateSyncProtocol.ValidIdentity(RecipientNodeId) || !Guid.TryParseExact(RequestId, "N", out _)
            || !Guid.TryParseExact(OldEpoch, "N", out _) || !Guid.TryParseExact(NewEpoch, "N", out _) || NewEpoch == OldEpoch
            || Scope is null || Scope.Domains is null || Scope.IncludedPrefixes is null || Scope.AllGroupPrefixes is null
            || Scope.Domains.Length == 0 || Scope.Domains.Any(d => !RuntimeStateSyncProtocol.ValidIdentity(d))
            || Scope.IncludedPrefixes.Concat(Scope.AllGroupPrefixes).Any(string.IsNullOrEmpty)
            || Items is null || Items.Any(m => m is null || !Scope.Contains(m))
            || Items.Select(m => m.Domain + "\n" + m.Key).Distinct().Count() != Items.Length
            || Digest != ComputeDigest())
            throw new InvalidDataException("Invalid runtime bootstrap image");
    }
}
