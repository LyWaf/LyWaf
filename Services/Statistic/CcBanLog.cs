namespace LyWaf.Services.Statistic;

public sealed record CcBanEntry(long Id, DateTimeOffset Time, string Ip, string Reason, double DurationSeconds);

/// <summary>Bounded process-local CC ban history; independent of current ban expiration.</summary>
public sealed class CcBanLog
{
    public static CcBanLog Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Queue<CcBanEntry> _entries = new();
    private long _sequence;

    public void Add(string ip, string reason, TimeSpan duration)
    {
        lock (_gate)
        {
            if (_entries.Count == 1000) _entries.Dequeue();
            _entries.Enqueue(new(++_sequence, DateTimeOffset.UtcNow, ip[..Math.Min(ip.Length, 128)],
                reason[..Math.Min(reason.Length, 1024)], duration.TotalSeconds));
        }
    }

    public CcBanEntry[] Read()
    {
        lock (_gate) return _entries.Reverse().ToArray();
    }
}
