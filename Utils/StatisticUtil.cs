
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using NLog;

namespace LyWaf.Utils;

public class StatisticUtil
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public static async Task DoStatisticRequest(HttpContext context, string destUrl, long costTime)
    {
        var statisticService = context.RequestServices.GetRequiredService<IStatisticService>();
        var path = await statisticService.GetMatchPath(context.Request.Path);
        _logger.Info("统计访问: 路径:{}, 实际归因:{}", context.Request.Path, path);
        bool func(string url, IpStatistic val)
        {
            if (val.UrlCostTime.TryGetValue(path, out var sub))
            {
                sub.IncrTime(costTime);
            }
            else
            {
                val.UrlCostTime[path] = new StaCountTime
                {
                    Count = 1,
                    UseTime = costTime,
                };
            }
            val.CountTime.IncrTime(costTime);
            var ct = val.UrlCostTime[path];
            _logger.Info("统计访问: 访问:{} 总次数:{} 总耗时:{}ms, 平均耗时: {}ms", url.TrimEnd('/') + path, ct.Count, ct.UseTime, ct.Average);
            return true;
        }

        SharedData.DestStas.DoLockKeyFunc(destUrl, (key) => new(), (val) => func(destUrl, val));
        var host = RequestUtil.GetRequestBaseUrl(context.Request);
        SharedData.ReqStas.DoLockKeyFunc(host, (key) => new(), (val) => func(host, val));
        var client_ip = RequestUtil.GetClientIp(context.Request);
        SharedData.ClientStas.DoLockKeyFunc(client_ip, (key) => new(), (val) => func(client_ip, val));
        
        // 白名单, 不进行后续的分析统计
        if(statisticService.IsWhitePath(path)) {
            return;
        }
        
        SharedData.NewClientVisits.Incr(client_ip, 1);
        SharedData.ClientDetailVisits.DoLockKeyFunc(client_ip, (key) => new(), (val) =>
        {
            val.AddLast(new ReqestShortMsg(context, path, costTime));
            return true;
        });
    }

    public static int GetPeriodIdx(int period, DateTime? time = null) {
        if(time == null) {
            time = DateTime.UtcNow;
        }
        return (int)((time.Value - DateTime.UnixEpoch).TotalSeconds / period);
    }
}