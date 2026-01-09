
using System.Threading.RateLimiting;

namespace LyWaf.Services.SpeedLimit;

public class SpeedLimitOptions
{
    public Dictionary<string, SpeedLimitPolicyOptions> Limits { get; set; } = [];

    public ThrottledOptions Throttled { get; set; } = new();
    public int RejectCode { get; set; } = 429;

    public string? Default { get; set; } = null;
}

public class SpeedLimitPolicyOptions
{
    public string Name { get; set; } = "fixed";
    public string? Partition { get; set; } = null;
    public int PermitLimit { get; set; } = 50;
    public int SegmentsPerWindow { get; set; } = 50;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    public int QueueLimit { get; set; } = 20;
    public QueueProcessingOrder QueueOrder { get; set; } = QueueProcessingOrder.OldestFirst;
    public TimeSpan? ReplenishmentPeriod { get; set; }
    public int TokensPerPeriod { get; set; } = 20;
}

public class ThrottledOptions
{
    public int Global { get; set; } = 0;
    public Dictionary<string, int> Everys { get; set; } = [];

    public Dictionary<string, int> IpEverys { get; set; } = [];
}

public class ClientThrottledLimit
{
    public TimeSpan Period = TimeSpan.FromSeconds(1);

    public int EveryCapacity = 1000000;

    public int LeftToken = 1000000;

    public DateTime LastRefillTime = DateTime.UtcNow;

    private const int MIN_STEP = 4;

    public int AllocToken(int token)
    {
        var now = DateTime.UtcNow;
        var timePassed = now - LastRefillTime;
        if(timePassed.TotalMilliseconds > Period.TotalMilliseconds / MIN_STEP) {
            var tokensToAdd = (int)((double)timePassed.TotalMilliseconds / Period.TotalMilliseconds * EveryCapacity);
            LastRefillTime = now;
            LeftToken = Math.Min(LeftToken + tokensToAdd, EveryCapacity);
        }

        var succ = Math.Min(LeftToken, token);
        LeftToken -= succ;
        return succ;
    }
}
