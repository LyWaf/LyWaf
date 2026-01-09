

namespace LyWaf.Services.Statistic;

public class StatisticOptions
{
    public HashSet<string> PathStas { get; set; } = [];
    public HashSet<string> WhitePaths { get; set; } = [];
    public Dictionary<string, object> Config { get; set; } = [];
    public List<LimitCcOption> LimitCc { get; set; } = [];
    public const string MATCH = "{**match}";
    public const string ANY = "{**any}";
    public const string CONNECT = "/C/";

    public const double DEFAULT_NOT_WAIT_FB_RATIO = 0.9;

    public int GetFbMinLen()
    {
        const int DEFAULT_VALUE = 50;
        if (Config.TryGetValue("fbLimit", out var val))
        {
            var len = Convert.ToInt32(val);
            return len == 0 ? DEFAULT_VALUE : len;
        }
        else
        {
            return DEFAULT_VALUE;
        }
    }

    public TimeSpan GetDefaultFbTime()
    {
        const int DEFAULT_VALUE = 600;
        if (Config.TryGetValue("defaultFbTime", out var val))
        {
            var len = Convert.ToInt32(val);
            return TimeSpan.FromSeconds(len == 0 ? DEFAULT_VALUE : len);
        }
        else
        {
            return TimeSpan.FromSeconds(DEFAULT_VALUE);
        }
    }

    public double GetNotWaitFbRatio()
    {
        const double DEFAULT_VALUE = 0.9;
        if (Config.TryGetValue("notWaitFbRatio", out var val))
        {
            var len = Convert.ToDouble(val);
            return len == 0 ? DEFAULT_VALUE : 0.9;
        }
        else
        {
            return DEFAULT_VALUE;
        }
    }


    public int GetMaxFreqGetNums()
    {
        const int DEFAULT_VALUE = 3;
        if (Config.TryGetValue("maxFreqGetNums", out var val))
        {
            var len = Convert.ToInt32(val);
            return len == 0 ? DEFAULT_VALUE : 3;
        }
        else
        {
            return DEFAULT_VALUE;
        }
    }


    public int GetMaxFreqMinReqs()
    {
        const int DEFAULT_VALUE = 100;
        if (Config.TryGetValue("maxFreqMinReqs", out var val))
        {
            var len = Convert.ToInt32(val);
            return len == 0 ? DEFAULT_VALUE : 3;
        }
        else
        {
            return DEFAULT_VALUE;
        }
    }

    public double GetMaxFreqFbRatio()
    {
        const double DEFAULT_VALUE = 0.9;
        if (Config.TryGetValue("maxFreqFbRatio", out var val))
        {
            var len = Convert.ToDouble(val);
            return len == 0 ? DEFAULT_VALUE : 0.9;
        }
        else
        {
            return DEFAULT_VALUE;
        }
    }

}

public class LimitCcOption
{
    public int Period { get; set; } = 60;

    public int LimitNum { get; set; } = 20;

    public string Path { get; set; } = "";

    public TimeSpan FbTime { get; set; } = TimeSpan.FromSeconds(600);
}
