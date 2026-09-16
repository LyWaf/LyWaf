namespace LyWaf.Services.Statistic;

/// <summary>Serializes CC ban admission so concurrent detectors record one transition per active ban.</summary>
public sealed class CcBanCoordinator(CcBanLog log)
{
    public static CcBanCoordinator Shared { get; } = new(CcBanLog.Shared);
    private readonly object _gate = new();

    public bool TryBan(string ip, string reason, TimeSpan duration, Func<bool> isBlocked, Action apply)
    {
        lock (_gate)
        {
            // Consult live state rather than cached expiry, so manual unblocking takes effect immediately.
            if (isBlocked()) return false;
            apply();
            log.Add(ip, reason, duration);
            return true;
        }
    }
}
