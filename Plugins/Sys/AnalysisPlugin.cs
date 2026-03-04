using LyWaf.Plugins.Core;
using LyWaf.Services;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using LyWaf.Utils;
using Microsoft.AspNetCore.Builder;
using NLog;

namespace LyWaf.Plugins.Sys;

/// <summary>
/// 系统插件：流量分析器
/// 后台运行，分析访问流量并进行 CC 攻击检测
/// </summary>
public class AnalysisPlugin : LyWafPluginBase
{
    private bool _isRunning = false;
    private Thread? _workerThread;
    private IDisposable? _eventSubscriptionComplete;

    public override PluginMetadata Metadata => new()
    {
        Id = "analysis",
        Name = "流量分析器",
        Version = "1.0.0",
        Description = "后台分析访问流量，检测 CC 攻击等异常行为",
        Author = "LyWaf Team",
        Priority = PluginPriority.High,  // 低优先级，后台运行
        EnabledByDefault = true
    };

    public override Task InitializeAsync(IPluginContext context)
    {
        context.Logger.Info("流量分析器已初始化");

        // 订阅请求完成事件（来自其他插件）
        _eventSubscriptionComplete = context.SubscribeEvent<RequestCompletedEvent>(async e =>
        {
            // await DoStaticLog(context, e);
            await Task.CompletedTask;
        });

        return base.InitializeAsync(context);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            return base.StartAsync(cancellationToken);
        }

        Context?.Logger.Info("启动流量分析后台任务...");
        _isRunning = true;

        _workerThread = new Thread(DoWork);
        _workerThread.Start();

        return base.StartAsync(cancellationToken);
    }

    public static async Task DoStaticLog(IPluginContext plugin, RequestCompletedEvent e)
    {
        var context = e.Context;
        context.Request.EnableBuffering();
        context.Items.TryGetValue("ProxyDestUrl", out var destUrl);
        long useTime = (long)e.Duration.TotalMilliseconds;
        await StatisticUtil.DoStatisticRequest(context, (string?)destUrl ?? "", useTime);
        
        // context.Request.Body.Position = 0;
        // Stream rawbody = new MemoryStream();
        // var request = context.Request;
        // await request.Body.CopyToAsync(rawbody, 200);
        // var body = StreamUtil.ConvertToString(rawbody);
        // var url = RequestUtil.GetRequestUrl(request);
        // var response = context.Response;
        // var reqRaw = RequestUtil.RecordRuquest(request, body);
        // plugin.Logger.Info("以下是curl指令:\n{}\n返回{},长度{}\n当前请求耗时: {} ms\n",
        //     reqRaw, response.StatusCode,
        //     response.ContentLength != null ? response.ContentLength : response.Headers.TransferEncoding,
        //     useTime);

    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        Context?.Logger.Info("停止流量分析后台任务...");
        _isRunning = false;

        // 等待工作线程结束
        _workerThread?.Join(TimeSpan.FromSeconds(5));
        _eventSubscriptionComplete?.Dispose();

        return base.StopAsync(cancellationToken);
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        proxyApp.UseMiddleware<StatisticLogMiddleware>();
    }

    /// <summary>
    /// 计算未等待响应就重复请求的次数
    /// </summary>
    private static (int notWaitCount, int allCount) CalcNotWaitCount(Dictionary<string, List<ReqestShortMsg>> allVisitTable)
    {
        var notWaitCount = 0;
        var allCount = 0;

        foreach (var (_, v) in allVisitTable)
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

    /// <summary>
    /// 后台分析工作循环
    /// </summary>
    private void DoWork()
    {
        while (_isRunning)
        {
            Thread.Sleep(100);
            if (!_isRunning)
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
                AnalyzeClientVisits(ip, option, min);
            }

            Context?.Logger.Info("完成数据分析: {Count} 个客户端", allNews.Count);
        }

        _isRunning = false;
    }

    /// <summary>
    /// 分析单个客户端的访问记录
    /// </summary>
    private void AnalyzeClientVisits(string ip, StatisticOptions option, int min)
    {
        List<ReqestShortMsg> requestList;
        lock (SharedData.ClientDetailVisits.GetLockObject())
        {
            if (!SharedData.ClientDetailVisits.TryGetValue(ip, out var val))
            {
                return;
            }
            if (val.Count < min) { return; }

            requestList = [.. val];
            val.Clear();
        }

        var isFb = false;
        Dictionary<string, List<ReqestShortMsg>> allVisitTable = [];
        var firstAccessTime = DateTime.MaxValue;
        var allVisitSize = requestList.Count;
        var lastAccessTime = DateTime.UtcNow;

        foreach (var req in requestList)
        {
            lastAccessTime = lastAccessTime > req.ReqTime ? lastAccessTime : req.ReqTime;
            firstAccessTime = firstAccessTime < req.ReqTime ? firstAccessTime : req.ReqTime;

            if (allVisitTable.TryGetValue(req.Path, out var urls))
            {
                urls.Add(req);
            }
            else
            {
                allVisitTable[req.Path] = [req];
            }

            // 检查 CC 限制
            foreach (var limit in option.LimitCc)
            {
                if (req.Path == limit.Path)
                {
                    var limitKey = string.Format("limitCount:{0}:{1}:{2}", ip, limit.Path, StatisticUtil.GetPeriodIdx(limit.Period, req.ReqTime));
                    var count = SharedData.LimitCcStas.Incr(limitKey, 1, 0, TimeSpan.FromSeconds(2 * limit.Period));
                    if (count > limit.LimitNum)
                    {
                        isFb = true;
                        WafUtil.DoFbIp(ip, $"超过{limit.Path}的CC的访问限制频率", limit.FbTime, 
                            eventType: Shared.SecurityEventType.CcAttack);
                        break;
                    }
                }
            }

            if (isFb) break;
        }

        if (isFb) return;

        // 检查未等待响应就重复请求的比例
        var (notWaitCount, allCount) = CalcNotWaitCount(allVisitTable);
        if (allCount < min) return;

        var ratio = option.GetNotWaitFbRatio();
        if (notWaitCount * 1.0 / allCount > ratio)
        {
            WafUtil.DoFbIp(ip, $"同一条请求未完成又重复请求占比 {notWaitCount * 1.0 / allCount}, 总次数 {allCount}",
                eventType: Shared.SecurityEventType.AbnormalBehavior);
            return;
        }

        // 访问间隔平均大于0.5秒/一条, 则均判定是正常
        var step = (lastAccessTime - firstAccessTime).TotalMilliseconds;
        if (step / allVisitSize > 500)
        {
            return;
        }

        // 检查高频请求集中度
        if (!SharedData.ClientStas.TryGetValue(ip, out var ipUrls))
        {
            return;
        }

        ipUrls = (IpStatistic)ipUrls.Clone();
        long allVisitTimes = 0;
        List<long> sortTimesList = [];

        foreach (var (_, times) in ipUrls.UrlCostTime)
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
            WafUtil.DoFbIp(ip, $"前{maxFreqGetNums}种请求({allMaxTimes * 1.0 / allVisitTimes}) 请求占比超过{maxRatio}",
                eventType: Shared.SecurityEventType.CcAttack);
            SharedData.ClientStas.Remove(ip);
        }
    }
}

/// <summary>
/// 统计日志中间件
/// 记录请求统计信息并进行数据采集
/// </summary>
public class StatisticLogMiddleware(RequestDelegate next)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        long timestamp_start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        context.Request.EnableBuffering();
        var end = context.GetEndpoint()!;

        await _next(context);
        context.Items.TryGetValue("ProxyDestUrl", out var destUrl);
        long timestamp_end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await StatisticUtil.DoStatisticRequest(context, (string?)destUrl ?? "", timestamp_end - timestamp_start);
        // context.Request.Body.Position = 0;
        // var request = context.Request;
        // string? body = null;
        // try {
        //     Stream rawbody = new MemoryStream();
        //     await request.Body.CopyToAsync(rawbody, 200);
        //     body = StreamUtil.ConvertToString(rawbody);
        // } catch(Exception) {
        // }
        // var response = context.Response;
        // var reqRaw = RequestUtil.RecordRuquest(request, body);
        // _logger.Info("以下是curl指令:\n{}\n返回{},长度{}\n当前请求耗时: {} ms\n",
        //     reqRaw, response.StatusCode,
        //     response.ContentLength != null ? response.ContentLength : response.Headers.TransferEncoding,
        //     timestamp_end - timestamp_start);
    }
}
