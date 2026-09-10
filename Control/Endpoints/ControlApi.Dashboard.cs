using System.Diagnostics;
using System.Text;
using LyWaf.Control.Endpoints;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Captcha;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Config;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Packaging;
using LyWaf.Services.Auth;
using LyWaf.Services.WafRule;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Acme;
using LyWaf.Shared;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.RuntimeState;
using LyWaf.Services;
using LyWaf.Policy;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapDashboardEndpoints(WebApplication app, int controlPort)
    {
        // =============== 仪表板数据 API ===============
        
        // 获取仪表板概览数据
        app.MapGet("/api/dashboard", (HttpContext ctx, 
            IAccessControlService accessControlService,
            IStatisticService statisticService,
            IProtectService protectService,
            IABTestService abTestService) =>
        {
                using var process = Process.GetCurrentProcess();
                var uptime = DateTime.Now - process.StartTime;
                var connectionStats = accessControlService.GetConnectionStats();
                
                // 获取配置选项
                var acOptions = accessControlService.GetOptions();
                var protectOptions = protectService.GetOptions();
                var statisticOptions = statisticService.GetOption();
                
                // 获取流量统计
                var trafficSnapshot = SharedData.Traffic.GetSnapshot();
                var geoSnapshot = SharedData.GeoTraffic.GetSnapshot();
                
                // 获取被封禁的IP（合并 ClientFb、CaptchaPending、ClientThrottled）
                var blockedIpList = SharedData.ClientFb.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "blocked",
                        reason = x.Value,
                        remainingSeconds = x.RemainingTime?.TotalSeconds
                    })
                    .Concat(SharedData.CaptchaPending.GetValidItemsWithExpiry()
                        .Select(x => new
                        {
                            ip = x.Key,
                            type = "captcha",
                            reason = $"验证码待验证: {x.Value.RuleName}",
                            remainingSeconds = x.RemainingTime?.TotalSeconds
                        }))
                    .Concat(SharedData.ClientThrottled.GetValidItemsWithExpiry()
                        .Select(x => new
                        {
                            ip = x.Key,
                            type = "throttled",
                            reason = $"带宽限速: {x.Value.EveryCapacity / 1024}KB/s",
                            remainingSeconds = x.RemainingTime?.TotalSeconds
                        }))
                    .Concat(SharedData.IpLogTargets.GetValidItemsWithExpiry()
                        .Select(x =>
                        {
                            var (exists, sizeBytes, _) = IpRequestLogger.GetLogFileInfo(x.Key);
                            return new
                            {
                                ip = x.Key,
                                type = "log",
                                reason = exists ? $"请求日志记录中 ({sizeBytes / 1024}KB)" : "请求日志记录中",
                                remainingSeconds = x.RemainingTime?.TotalSeconds
                            };
                        }))
                    .ToList();

                // 获取最近5分钟内的客户端
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var threshold = now - (5 * 60 * 1000);
                var recentClients = SharedData.ClientStas.GetSnapshot()
                    .Where(kv => kv.Value.LastAccessTime >= threshold)
                    .Select(kv => new
                    {
                        ip = kv.Key,
                        lastAccessTime = DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.LastAccessTime).LocalDateTime
                    })
                    .OrderByDescending(x => x.lastAccessTime)
                    .Take(50)
                    .ToList();
                
                // 格式化运行时间
                var uptimeStr = uptime.Days > 0 
                    ? $"{uptime.Days}天 {uptime.Hours}小时 {uptime.Minutes}分钟" 
                    : uptime.Hours > 0 
                        ? $"{uptime.Hours}小时 {uptime.Minutes}分钟 {uptime.Seconds}秒"
                        : $"{uptime.Minutes}分钟 {uptime.Seconds}秒";

                return TypedResults.Ok(new
                {
                    success = true,
                    geoTraffic = new
                    {
                        countryVisits = geoSnapshot.CountryVisits.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, value = x.Value }),
                        countryIntercepts = geoSnapshot.CountryIntercepts.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, value = x.Value }),
                        regionVisits = geoSnapshot.RegionVisits.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, value = x.Value }),
                        regionIntercepts = geoSnapshot.RegionIntercepts.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, value = x.Value })
                    },
                    system = new
                    {
                        uptime = uptimeStr,
                        memory = process.WorkingSet64 / (1024 * 1024),
                        totalConnections = ConnectionTracker.GetActiveCount(),
                        httpConnections = ConnectionTracker.GetHttpCount(),
                        wsConnections = ConnectionTracker.GetWebSocketCount(),
                        blockedIpCount = blockedIpList.Count,
                        processStartTime = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        uniqueIps = connectionStats.ConnectionsPerIp.Count
                    },
                    traffic = new
                    {
                        totalRequests = trafficSnapshot.TotalRequests,
                        pageViews = trafficSnapshot.PageViews,
                        uniqueVisitors = trafficSnapshot.UniqueVisitors,
                        uniqueIps = trafficSnapshot.UniqueIps,
                        interceptCount = trafficSnapshot.InterceptCount,
                        attackIps = trafficSnapshot.AttackIps,
                        error4xxCount = trafficSnapshot.Error4xxCount,
                        error4xxRate = trafficSnapshot.Error4xxRate,
                        intercept4xxCount = trafficSnapshot.Intercept4xxCount,
                        intercept4xxRate = trafficSnapshot.Intercept4xxRate,
                        error5xxCount = trafficSnapshot.Error5xxCount,
                        error5xxRate = trafficSnapshot.Error5xxRate,
                        startTime = trafficSnapshot.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    },
                    features = new
                    {
                        ipControl = acOptions.IpControl.Enabled,
                        geoControl = acOptions.GeoControl.Enabled,
                        connectionLimit = acOptions.ConnectionLimit.Enabled,
                        wafArgs = protectOptions.OpenArgsCheck,
                        wafPost = protectOptions.OpenPostCheck,
                        ccProtection = statisticOptions.LimitCc.Count > 0 || statisticService.GetLimitCcRules().Count > 0
                    },
                    recentClients,
                    whitelist = accessControlService.GetWhitelist(),
                    blacklist = accessControlService.GetBlacklist(),
                    geoAccess = new
                    {
                        allowCountries = accessControlService.GetAllowCountries(),
                        allowRegions = accessControlService.GetAllowRegions(),
                        denyCountries = accessControlService.GetDenyCountries(),
                        denyRegions = accessControlService.GetDenyRegions()
                    },
                    wafRules = new
                    {
                        args = protectService.GetArgsRegexList(),
                        post = protectService.GetPostRegexList(),
                        argsFileCount = protectService.GetArgsFilePatternCount(),
                        postFileCount = protectService.GetPostFilePatternCount()
                    },
                    ccRules = statisticService.GetLimitCcRules().Select(r => new
                    {
                        path = r.Path,
                        period = r.Period,
                        limitNum = r.LimitNum,
                        fbTime = r.FbTime.TotalSeconds
                    }),
                    blockedIps = blockedIpList,
                    abTests = abTestService.GetAllConfigs().Select(c => new
                    {
                        testId = c.Key,
                        name = c.Value.Name,
                        enabled = c.Value.Enabled,
                        mode = c.Value.Mode.ToString(),
                        variants = c.Value.Variants
                    }),
                    timestamp = DateTime.Now
                });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取流量统计数据
        app.MapGet("/api/traffic/stats", (HttpContext ctx) =>
        {
                var trafficSnapshot = SharedData.Traffic.GetSnapshot();
                return TypedResults.Ok(new
                {
                    success = true,
                    totalRequests = trafficSnapshot.TotalRequests,
                    pageViews = trafficSnapshot.PageViews,
                    uniqueVisitors = trafficSnapshot.UniqueVisitors,
                    uniqueIps = trafficSnapshot.UniqueIps,
                    interceptCount = trafficSnapshot.InterceptCount,
                    attackIps = trafficSnapshot.AttackIps,
                    error4xxCount = trafficSnapshot.Error4xxCount,
                    error4xxRate = trafficSnapshot.Error4xxRate,
                    intercept4xxCount = trafficSnapshot.Intercept4xxCount,
                    intercept4xxRate = trafficSnapshot.Intercept4xxRate,
                    error5xxCount = trafficSnapshot.Error5xxCount,
                    error5xxRate = trafficSnapshot.Error5xxRate,
                    startTime = trafficSnapshot.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    timestamp = DateTime.Now
                });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 安全态势统计数据
        app.MapGet("/api/security/stats", (HttpContext ctx) =>
        {
                var hoursStr = ctx.Request.Query["hours"].FirstOrDefault();
                var hours = int.TryParse(hoursStr, out var h) ? h : 24;
                
                var snapshot = SharedData.Security.GetSnapshot();
                var timeSlots = SharedData.Security.GetTimeSlots(hours);
                var topAttackSources = SharedData.Security.GetTopAttackSources(10);
                
                // 各类型的 Top IP
                var topWafSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.WafIntercept, 5);
                var topCcSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.CcAttack, 5);
                var topBlacklistSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.BlacklistBlock, 5);
                var topGeoSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.GeoBlock, 5);
                var topCrawlerSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.CrawlerDetect, 5);
                
                return TypedResults.Ok(new
                {
                    snapshot = new
                    {
                        startTime = snapshot.StartTime,
                        wafInterceptCount = snapshot.WafInterceptCount,
                        blacklistBlockCount = snapshot.BlacklistBlockCount,
                        ccAttackCount = snapshot.CcAttackCount,
                        crawlerDetectCount = snapshot.CrawlerDetectCount,
                        geoBlockCount = snapshot.GeoBlockCount,
                        rateLimitCount = snapshot.RateLimitCount,
                        totalInterceptCount = snapshot.TotalInterceptCount,
                        uniqueAttackIps = snapshot.UniqueAttackIps
                    },
                    timeSlots = timeSlots.Select(s => new
                    {
                        time = s.Time,
                        wafIntercept = s.WafIntercept,
                        blacklistBlock = s.BlacklistBlock,
                        ccAttack = s.CcAttack,
                        crawlerDetect = s.CrawlerDetect,
                        geoBlock = s.GeoBlock,
                        rateLimit = s.RateLimit,
                        total = s.Total
                    }),
                    topAttackSources = topAttackSources.Select(s => new { ip = s.Ip, count = s.Count }),
                    topWafSources = topWafSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topCcSources = topCcSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topBlacklistSources = topBlacklistSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topGeoSources = topGeoSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topCrawlerSources = topCrawlerSources.Select(s => new { ip = s.Item1, count = s.Item2 })
                });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // QPS 历史数据（实时用内存，历史用 DuckDB）
        app.MapGet("/api/qps/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
                var granularity = HistoryQuery.Choice(ctx.Request.Query, "granularity", "5s", "5s", "1min", "1hour");
                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();

                // 如果指定了 from/to，从 DuckDB 查历史
                if (fromStr != null || toStr != null)
                {
                    if (!queryService.IsEnabled)
                        return Results.Json(new { success = false, message = "DuckDB 未启用" });

                    var (from, to) = HistoryQuery.Range(ctx.Request.Query, TimeSpan.FromDays(1));
                    var dbGranularity = granularity switch
                    {
                        "5s" => "1min",
                        "1min" => "1min",
                        "1hour" => "5min",
                        _ => "1min"
                    };
                    var rows = queryService.GetQpsHistory(from, to, dbGranularity);
                    var stepSeconds = dbGranularity == "5min" ? 300.0 : 60.0;
                    var data = rows.Select(r => new
                    {
                        time = r.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"),
                        qps = Math.Round(r.RequestCount / stepSeconds, 2),
                        requestCount = r.RequestCount
                    });
                    return Results.Json(new { success = true, data, granularity, source = "duckdb" });
                }

                // 否则从内存取实时数据
                var memData = SharedData.Qps.GetHistory(granularity);
                return Results.Json(new { success = true, data = memData, granularity, source = "memory" });
        }).Produces<RealtimeHistoryResponse<QpsDataPoint>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 带宽历史
        app.MapGet("/api/bandwidth/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
                var granularity = HistoryQuery.Choice(ctx.Request.Query, "granularity", "5s", "5s", "1min", "1hour");
                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();

                if (fromStr != null || toStr != null)
                {
                    if (!queryService.IsEnabled)
                        return Results.Json(new { success = false, message = "DuckDB 未启用" });

                    var (from, to) = HistoryQuery.Range(ctx.Request.Query, TimeSpan.FromDays(1));
                    var dbGranularity = granularity switch
                    {
                        "5s" => "1min",
                        "1min" => "1min",
                        "1hour" => "5min",
                        _ => "1min"
                    };
                    var stepSeconds = dbGranularity == "5min" ? 300.0 : 60.0;
                    var rows = queryService.GetBandwidthHistory(from, to, dbGranularity);
                    var data = rows.Select(r => new
                    {
                        time = r.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"),
                        inboundBytes = r.InboundBytes,
                        outboundBytes = r.OutboundBytes,
                        inboundRate = Math.Round(r.InboundBytes / stepSeconds, 2),
                        outboundRate = Math.Round(r.OutboundBytes / stepSeconds, 2),
                    });
                    return Results.Json(new { success = true, data, granularity, source = "duckdb" });
                }

                var memData = SharedData.Bandwidth.GetHistory(granularity);
                return Results.Json(new { success = true, data = memData, granularity, source = "memory" });
        }).Produces<RealtimeHistoryResponse<BandwidthDataPoint>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 重置安全态势统计数据
        app.MapPost("/api/security/reset", (HttpContext ctx) =>
        {
                SharedData.Security.Reset();
                return TypedResults.Ok(new { success = true, message = "安全态势统计数据已重置" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/status", (HttpContext ctx) =>
        {
            return TypedResults.Ok(new
            {
                status = "running",
                pid = Environment.ProcessId,
                uptime = DateTime.Now - Process.GetCurrentProcess().StartTime,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();
        
        app.MapGet("/api/info", (HttpContext ctx) =>
        {
            var process = Process.GetCurrentProcess();
            return TypedResults.Ok(new
            {
                pid = process.Id,
                name = process.ProcessName,
                startTime = process.StartTime,
                memoryMB = process.WorkingSet64 / (1024 * 1024),
                threads = process.Threads.Count
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
