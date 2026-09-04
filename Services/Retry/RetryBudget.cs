namespace LyWaf.Services.Retry;

internal sealed class RetryBudget
{
    private const int WindowSeconds = 10;
    private readonly long[] _requests = new long[WindowSeconds];
    private readonly long[] _retries = new long[WindowSeconds];
    private readonly object _lock = new();
    private long _currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void RecordRequest()
    {
        lock (_lock)
        {
            Advance();
            _requests[Index(_currentSecond)]++;
        }
    }

    public bool TryAcquireRetry(int percent, int minRetriesPerSecond)
    {
        lock (_lock)
        {
            Advance();
            var requests = _requests.Sum();
            var retries = _retries.Sum();
            var allowed = Math.Max((long)minRetriesPerSecond * WindowSeconds,
                (long)Math.Ceiling(requests * percent / 100d));
            if (retries >= allowed) return false;
            _retries[Index(_currentSecond)]++;
            return true;
        }
    }

    private void Advance()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var elapsed = Math.Min(now - _currentSecond, WindowSeconds);
        for (var i = 1L; i <= elapsed; i++)
        {
            var index = Index(_currentSecond + i);
            _requests[index] = 0;
            _retries[index] = 0;
        }
        if (now > _currentSecond) _currentSecond = now;
    }

    private static int Index(long second) => (int)(second % WindowSeconds);
}
