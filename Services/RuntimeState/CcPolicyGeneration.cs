using System.Globalization;

namespace LyWaf.Services.RuntimeState;

/// <summary>A monotonic CC release fence, independent of transport sequence and wall clocks.</summary>
public sealed record CcPolicyGeneration(long Version, string ReleaseId) : IComparable<CcPolicyGeneration>
{
    public const string Domain = "cc.policy-generation";
    public static CcPolicyGeneration Initial { get; } = new(0, "");

    public int CompareTo(CcPolicyGeneration? other)
    {
        if (other is null) return 1;
        var version = Version.CompareTo(other.Version);
        return version != 0 ? version : string.CompareOrdinal(ReleaseId, other.ReleaseId);
    }

    public string StateKey(string policyKey) => Version == 0 ? policyKey
        : $"ccg:{Version.ToString(CultureInfo.InvariantCulture)}:{ReleaseId}:{policyKey}";

    internal static bool IsStateDomain(string domain) => domain is
        "cc.triggered" or "cc.access" or "cc.attack" or "cc.error" or "cc.ban.logged" or "cc.policy" or "cc.decision";

    internal static bool TryParseKey(string key, out string policyKey, out CcPolicyGeneration generation)
    {
        // Window suffixes are not part of the subject's policy identity.
        var window = key.LastIndexOf('|');
        if (window >= 0) key = key[..window];
        policyKey = key;
        generation = Initial;
        if (key.StartsWith("ccg:", StringComparison.Ordinal))
        {
            var parts = key.Split(':', 4);
            if (parts.Length != 4 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version <= 0 || !Guid.TryParseExact(parts[2], "N", out _)) return false;
            generation = new(version, parts[2]);
            policyKey = parts[3];
        }
        return policyKey.StartsWith("v2:", StringComparison.Ordinal);
    }
}
