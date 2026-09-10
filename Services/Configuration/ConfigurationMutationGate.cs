namespace LyWaf.Services.Configuration;

/// <summary>Serializes patch read-modify-publish and recovery across management business modules.</summary>
internal static class ConfigurationMutationGate
{
    internal static SemaphoreSlim Patch { get; } = new(1, 1);
}
