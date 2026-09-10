namespace LyWaf.Policy;

/// <summary>Identifies scheduled probes so their deadline cancellation is handled as a probe failure.</summary>
internal static class HealthProbeRequestOptions
{
    public static readonly HttpRequestOptionsKey<bool> IsProbe = new("LyWaf.HealthProbe");
    public static bool Contains(HttpRequestMessage request)
        => request.Options.TryGetValue(IsProbe, out var value) && value;
}
