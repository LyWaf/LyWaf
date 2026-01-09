
using LyWaf.Services;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using LyWaf.Utils;
using NLog;

namespace LyWaf.Backend;

public class Analysis
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private static bool IsInWork = true;

    public static (int, int) CalcNotWaitCount(Dictionary<string, List<ReqestShortMsg>> allVisitTable)
    {
        var notWaitCount = 0;
        var allCount = 0;
        foreach (var (k, v) in allVisitTable)
        {
            allCount += v.Count;
            v.Sort((a, b) => a.ReqTime.CompareTo(b.ReqTime));
            long allCostTime = 0;
            foreach (var t in v)
            {
                allCostTime += t.CostTime;
            }
            var ignoreCost = allCostTime * 3 / v.Count;
            DateTime? nextRequestTime = null;
            foreach (var t in v)
            {
                if (t.CostTime > ignoreCost)
                {
                    continue;
                }
                var next = t.ReqTime + TimeSpan.FromMilliseconds(t.CostTime);
                if (nextRequestTime == null)
                {
                    nextRequestTime = next;
                }
                else
                {
                    if (t.ReqTime < nextRequestTime)
                    {
                        notWaitCount += 1;
                    }
                    nextRequestTime = next > nextRequestTime ? next : nextRequestTime;
                }
            }
        }
        return (notWaitCount, allCount);
    }
    public static void DoWork()
    {
        while (IsInWork)
        {
            Thread.Sleep(100);
            if (!IsInWork)
            {
                break;
            }

            var statisticService = ServiceLocator.GetRequiredService<IStatisticService>();
            if (statisticService == null)
            {
                continue;
            }
            var option = statisticService.GetOption();
            var min = option.GetFbMinLen();
            var allNews = SharedData.NewClientVisits.FilterRemove((v) =>
            {
                return v.Item2 > min;
            });
            if (allNews.Count == 0)
            {
                continue;
            }

            foreach (var (ip, _) in allNews)
            {
                List<ReqestShortMsg> requestList;
                lock (SharedData.ClientDetailVisits.GetLockObject())
                {
                    if (!SharedData.ClientDetailVisits.TryGetValue(ip, out var val))
                    {
                        continue;
                    }
                    if (val.Count < min) { continue; }

                    requestList = [.. val];
                    val.Clear();
                }

                var isFb = false;
                Dictionary<string, List<ReqestShortMsg>> allVisitTable = [];
                var lastPath = "";
                var firstAccessTime = DateTime.MaxValue;
                var allVisitSize = requestList.Count;
                var lastAccessTime = DateTime.UtcNow;

                foreach (var req in requestList)
                {
                    lastAccessTime = lastAccessTime > req.ReqTime ? lastAccessTime : req.ReqTime;
                    firstAccessTime = firstAccessTime < req.ReqTime ? firstAccessTime : req.ReqTime;
                    lastPath = req.Path;
                    if (allVisitTable.TryGetValue(req.Path, out var urls))
                    {
                        urls.Add(req);
                    }
                    else
                    {
                        allVisitTable[req.Path] = [req];
                    }

                    foreach (var limit in option.LimitCc)
                    {
                        if (req.Path == limit.Path)
                        {
                            var limitKey = string.Format("limitCount:{0}:{1}:{2}", ip, limit.Path, StatisticUtil.GetPeriodIdx(limit.Period, req.ReqTime));
                            var count = SharedData.LimitCcStas.Incr(limitKey, 1, 0, TimeSpan.FromSeconds(2 * limit.Period));
                            if (count > limit.LimitNum)
                            {
                                isFb = true;
                                WafUtil.DoFbIp(ip, $"超过{limit.Path}的CC的访问限制频率", limit.FbTime);
                                break;
                            }
                        }
                    }
                    if (isFb)
                    {
                        break;
                    }
                }

                if (isFb)
                {
                    continue;
                }
                var (notWaitCount, allCount) = CalcNotWaitCount(allVisitTable);
                if (allCount < min) { continue; }

                var ratio = option.GetNotWaitFbRatio();
                if (notWaitCount * 1.0 / allCount > ratio)
                {
                    WafUtil.DoFbIp(ip, $"同一条请求未完成又重复请求占比 {notWaitCount * 1.0 / allCount}, 总次数 {allCount}");
                    continue;
                }

                var step = (lastAccessTime - firstAccessTime).TotalMilliseconds;
                // 访问间隔平均大于0.5秒/一条, 则均判定是正常
                if (step / allVisitSize > 500)
                {
                    continue;
                }

                if (!SharedData.ClientStas.TryGetValue(ip, out var ipUrls))
                {
                    continue;
                }
                ipUrls = (IpStatistic)ipUrls.Clone();
                long allVisitTimes = 0;
                List<long> sortTimesList = [];
                foreach (var (url, times) in ipUrls.UrlCostTime)
                {
                    allVisitTimes += times.Count;
                    sortTimesList.Add(times.Count);
                }
                sortTimesList.Sort((a, b) => b.CompareTo(a));
                long allMaxTimes = 0;
                var maxFreqGetNums = option.GetMaxFreqGetNums();
                for (int i = 0; i < Math.Min(maxFreqGetNums, sortTimesList.Count); i++)
                {
                    allMaxTimes += sortTimesList[i];
                }
                var maxRatio = allMaxTimes * 1.0 / allVisitTimes;
                if (allVisitTimes > option.GetMaxFreqMinReqs() && maxRatio > option.GetMaxFreqFbRatio())
                {
                    WafUtil.DoFbIp(ip, $"前{maxFreqGetNums}种请求({allMaxTimes * 1.0 / allVisitTimes}) 请求占比超过{maxRatio}");
                    SharedData.ClientStas.Remove(ip);
                }
            }

            _logger.Info("开始数据分析: {}", allNews.Count);
        }
        IsInWork = false;
    }
    public static void DoStartAnalysis()
    {
        // 控制台Ctrl+C/Ctrl+Break
        Console.CancelKeyPress += OnCancelKeyPress;
        Thread thread = new(DoWork);
        thread.Start();
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        _logger.Info("接收到终止信号，清理PID文件...");
        e.Cancel = true; // 允许优雅退出
        IsInWork = false;
    }

    public static void DoStopWork()
    {
        IsInWork = false;
    }
}