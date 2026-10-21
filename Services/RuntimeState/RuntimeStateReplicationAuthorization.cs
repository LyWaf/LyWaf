using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

/// <summary>Hop authorization for local mutations and explicitly trusted anti-entropy relays.</summary>
public static class RuntimeStateReplicationAuthorization
{
    public static bool IsValidConfiguration(RuntimeStateOptions options)
    {
        if (options.ProtectionGroupKeyPrefixes is null || options.Peers is null) return false;
        var bindings = options.ProtectionGroupKeyPrefixes;
        if (bindings.Any(pair => !ValidId(pair.Key) || pair.Value is null || pair.Value.Count == 0
            || pair.Value.Any(prefix => string.IsNullOrWhiteSpace(prefix) || prefix.Length > 1024))) return false;
        var prefixes = bindings.SelectMany(pair => pair.Value.Select(prefix => (Group: pair.Key, Prefix: prefix))).ToArray();
        for (var i = 0; i < prefixes.Length; i++)
        for (var j = i + 1; j < prefixes.Length; j++)
            if (prefixes[i].Group != prefixes[j].Group
                && (prefixes[i].Prefix.StartsWith(prefixes[j].Prefix, StringComparison.Ordinal)
                    || prefixes[j].Prefix.StartsWith(prefixes[i].Prefix, StringComparison.Ordinal))) return false;
        bool ValidPolicy(RuntimeStateReplicationPolicy? policy) => policy is not null
            && policy.AllowedDomains is not null && policy.AllowedDomains.All(ValidId)
            && policy.AllowedProtectionGroups is not null && policy.AllowedProtectionGroups.All(group =>
                ValidId(group) && bindings.Keys.Contains(group, StringComparer.Ordinal))
            && policy.TrustedForwardedSources is not null && policy.TrustedForwardedSources.All(ValidId);
        return options.Peers.All(pair => ValidId(pair.Key) && pair.Value is not null
            && (!pair.Value.DurableSyncEnabled || pair.Value.ProtocolVersion == 2)
            && ValidPolicy(pair.Value.Send) && ValidPolicy(pair.Value.Receive));
    }

    public static string? AuthorizeReceive(RuntimeStateOptions options, string sender,
        IEnumerable<RuntimeStateMutation> mutations, bool durableSync = false)
    {
        if (!options.Enabled || !TryPeer(options, sender, out var peer)) return "runtime_peer_forbidden";
        if (peer.DurableSyncEnabled && !durableSync) return "runtime_sync_required";
        foreach (var mutation in mutations)
        {
            var error = Authorize(options, peer.Receive, sender, mutation,
                options.ManagedTopologyRole == "Node" && sender == options.HubControllerNodeId);
            if (error is not null) return error;
        }
        return null;
    }

    public static string? AuthorizeSend(RuntimeStateOptions options, string receiver, RuntimeStateMutation mutation)
    {
        if (mutation is null) return "runtime_invalid_identity";
        if (!options.Enabled || !TryPeer(options, receiver, out var peer)) return "runtime_peer_forbidden";
        var hub = options.ManagedTopologyRole == "Controller";
        if (options.ManagedTopologyRole == "Node" && receiver == options.HubControllerNodeId && mutation.CounterComponents is not null)
        {
            if (!RuntimeStateHub.HasOwnCounter(mutation, options.NodeId)) return "runtime_counter_source_forbidden";
            hub = true;
        }
        return Authorize(options, peer.Send, options.NodeId, mutation, hub);
    }

    private static bool TryPeer(RuntimeStateOptions options, string id, out RuntimeStatePeerOptions peer)
    {
        // RuntimeState historically uses an ignore-case dictionary. Wire identities must remain exact.
        peer = options.Peers.FirstOrDefault(pair => pair.Key == id).Value!;
        return ValidId(id) && peer is { Enabled: true };
    }

    private static string? Authorize(RuntimeStateOptions options, RuntimeStateReplicationPolicy? policy,
        string directSource, RuntimeStateMutation? mutation, bool hubForward = false)
    {
        if (mutation is null || string.IsNullOrWhiteSpace(mutation.Domain) || string.IsNullOrWhiteSpace(mutation.Key))
            return "runtime_invalid_identity";
        if (policy?.AllowedDomains?.Contains(mutation.Domain, StringComparer.Ordinal) != true)
            return "runtime_domain_forbidden";
        if (options.ProtectionGroupKeyPrefixes is null) return "runtime_group_forbidden";
        var key = mutation.Key;
        if (key.StartsWith("ccg:", StringComparison.Ordinal))
        {
            if (!CcPolicyGeneration.TryParseKey(key, out key, out _)) return "runtime_group_forbidden";
        }
        string? group = null;
        foreach (var binding in options.ProtectionGroupKeyPrefixes)
        {
            if (binding.Value is null || !binding.Value.Any(prefix => !string.IsNullOrEmpty(prefix)
                && key.StartsWith(prefix, StringComparison.Ordinal))) continue;
            if (group is not null && group != binding.Key) return "runtime_group_ambiguous";
            group = binding.Key;
        }
        if (group is null ? !policy.AllowUngroupedKeys
            : policy.AllowedProtectionGroups?.Contains(group, StringComparer.Ordinal) != true)
            return "runtime_group_forbidden";

        bool SourceAllowed(string source) => ValidId(source) && (source == directSource
            || policy.TrustedForwardedSources?.Contains(source, StringComparer.Ordinal) == true
            || hubForward && options.HubSourceIsKnown?.Invoke(source) == true);
        if (!SourceAllowed(mutation.NodeId)) return "runtime_source_forbidden";
        if (mutation.CounterComponents is not null)
        {
            foreach (var component in mutation.CounterComponents.Keys)
                if (!TryComponentSource(component, out var source) || !SourceAllowed(source))
                    return "runtime_counter_source_forbidden";
        }
        return null;
    }

    internal static bool TryComponentSource(string component, out string source)
    {
        source = component;
        // Older components used the exact NodeId. New ones encode [NodeId, process epoch].
        if (ValidId(component)) return true;
        if (component is null || component.Length > 512 || !component.StartsWith("[", StringComparison.Ordinal)) return false;
        try
        {
            using var json = JsonDocument.Parse(component);
            var parts = json.RootElement;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 2
                || parts[0].ValueKind != JsonValueKind.String || parts[1].ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(parts[1].GetString(), "N", out var epoch)) return false;
            source = parts[0].GetString()!;
            return ValidId(source) && component == JsonSerializer.Serialize(new[] { source, epoch.ToString("N") });
        }
        catch (JsonException) { return false; }
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
