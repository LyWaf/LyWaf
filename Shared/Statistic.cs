
using System.Security.Cryptography;

namespace LyWaf.Shared;

public class StaCountTime : ICloneable
{
    public int Count { get; set; } = 0;

    public long UseTime { get; set; } = 0;

    public void IncrTime(long time)
    {
        Count++;
        UseTime += time;
    }

    public object Clone()
    {
        return MemberwiseClone();
        // return new StaCountTime {
        //     Count=Count,
        //     UseTime=UseTime,
        // };
    }

    public double Average
    {
        get
        {
            return UseTime / Math.Max(Count, 1);
        }
    }
}

public class IpStatistic : ICloneable
{
    // 比如 /api/config (10, 1000)
    public Dictionary<string, StaCountTime> UrlCostTime = [];

    public StaCountTime CountTime { get; set; } = new();

    public object Clone()
    {
        return new IpStatistic
        {
            CountTime = (StaCountTime)CountTime.Clone(),
            UrlCostTime = new Dictionary<string, StaCountTime>(UrlCostTime),
        };
    }
}

public class ClientStatistic
{
    // 比如 /api/config (10, 1000)
    public Dictionary<string, int> UrlVisitTimes = [];

    public StaCountTime CountTime { get; set; } = new();
}

public class ReqestShortMsg(HttpContext context, string path, long costTime)
{
    public string Path { get; set; } = path;
    public long CostTime { get; set; } = costTime;
    public DateTime ReqTime { get; set; } = DateTime.UtcNow;
    public int StatusCode { get; set; } = context.Response.StatusCode;
}
