namespace LyWaf.Services.RuntimeState;

/// <summary>Normalize counters at the authority boundary, without changing the authenticated wire batch or its receipt digest.</summary>
internal static class RuntimeStateHub
{
    internal static IReadOnlyList<RuntimeStateMutation> ProjectIncoming(RuntimeStateOptions options, string sender,
        IReadOnlyList<RuntimeStateMutation> mutations)
    {
        if (options.ManagedTopologyRole != "Controller") return mutations;
        return mutations.Select(mutation =>
        {
            if (mutation.CounterComponents is not { } components) return mutation;
            if (components.Any(pair => pair.Value < 0 || !RuntimeStateReplicationAuthorization.TryComponentSource(pair.Key, out _)))
                throw new UnauthorizedAccessException("Invalid counter source");
            var own = components.Where(pair => RuntimeStateReplicationAuthorization.TryComponentSource(pair.Key, out var source)
                && source == sender).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (own.Count == 0) throw new UnauthorizedAccessException("Missing sender counter component");
            return mutation with { NodeId = sender, CounterComponents = own };
        }).ToArray();
    }

    internal static bool HasOwnCounter(RuntimeStateMutation mutation, string node) => mutation.CounterComponents?.Keys.Any(
        component => RuntimeStateReplicationAuthorization.TryComponentSource(component, out var source) && source == node) == true;
}
