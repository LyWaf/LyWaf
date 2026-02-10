using System.Diagnostics;
using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Shared;
using Microsoft.Extensions.FileProviders;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LyWaf.Control;

public static class ControlApi
{
    private static readonly HashSet<string> AllowedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileProvider", "Logging", "Protect", "ReverseProxy", 
        "SpeedLimit", "Statistic", "WafInfos"
    };
    
    // 缓存前端静态文件
    private static string? _indexHtmlCache;
    private static DateTime _indexHtmlLastModified;
    
    /// <summary>
    /// 获取前端静态文件目录
    /// </summary>
    private static string GetFrontendDistPath()
    {
        // 首先尝试 control_html 目录（构建后输出目录）
        var controlHtmlPath = Path.Combine(Directory.GetCurrentDirectory(), "control_html");
        if (Directory.Exists(controlHtmlPath)) return controlHtmlPath;
        
        // 然后尝试 Frontend/dist 目录（开发环境）
        var devPath = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "dist");
        if (Directory.Exists(devPath)) return devPath;
        
        // 然后尝试 BaseDirectory 下的 control_html（发布环境）
        var releasePath = Path.Combine(AppContext.BaseDirectory, "control_html");
        if (Directory.Exists(releasePath)) return releasePath;
        
        // 最后返回 BaseDirectory 下的 frontend
        return Path.Combine(AppContext.BaseDirectory, "frontend");
    }
    
    /// <summary>
    /// 获取 index.html 内容
    /// </summary>
    private static string GetIndexHtml()
    {
        var distPath = GetFrontendDistPath();
        var indexPath = Path.Combine(distPath, "index.html");
        
        if (!File.Exists(indexPath))
        {
            return @"<!DOCTYPE html>
<html><head><meta charset=""UTF-8""><title>LyWaf 控制台</title></head>
<body><h1>前端文件未找到</h1><p>请先构建前端: cd Frontend && npm run build</p></body></html>";
        }
        
        var lastModified = File.GetLastWriteTime(indexPath);
        if (_indexHtmlCache == null || lastModified > _indexHtmlLastModified)
        {
            _indexHtmlCache = File.ReadAllText(indexPath, Encoding.UTF8);
            _indexHtmlLastModified = lastModified;
        }
        
        return _indexHtmlCache;
    }
    
    /// <summary>
    /// 注册控制台 API 路由
    /// </summary>
    public static WebApplication MapControlApi(this WebApplication app, WafInfoOptions wafInfos)
    {
        var controlListen = wafInfos.GetControlListen();
        var controlPort = controlListen.Port;
        
        // =============== 前端静态文件服务 ===============
        
        var distPath = GetFrontendDistPath();
        if (Directory.Exists(distPath))
        {
            // 静态资源文件服务（JS/CSS/图片等）
            var fileProvider = new PhysicalFileProvider(distPath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = "",
                ServeUnknownFileTypes = false
            });
        }
        
        // SPA 入口 - 所有非 API 请求返回 index.html
        app.MapGet("/", (HttpContext ctx) =>
        {
            return Results.Content(GetIndexHtml(), "text/html; charset=utf-8");
        }).RequireHost($"*:{controlPort}");
        
        // SPA 子路由支持（security, api-timing 等）
        app.MapGet("/{*path}", (HttpContext ctx, string? path) =>
        {
            // 如果是 API 跳过
            if (path != null && path.StartsWith("api/"))
            {
                return Results.NotFound();
            }
            
            // 检查是否是静态文件
            if (path != null)
            {
                var distDir = GetFrontendDistPath();
                var filePath = Path.Combine(distDir, path);
                if (File.Exists(filePath))
                {
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    var contentType = ext switch
                    {
                        ".js" => "application/javascript",
                        ".css" => "text/css",
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".ico" => "image/x-icon",
                        ".woff" or ".woff2" => "font/woff2",
                        _ => "application/octet-stream"
                    };
                    return Results.File(filePath, contentType);
                }
            }
            
            // 返回 SPA index.html
            return Results.Content(GetIndexHtml(), "text/html; charset=utf-8");
        }).RequireHost($"*:{controlPort}");

        // API 耗时统计数据列表
        app.MapGet("/api/timing/list", (HttpContext ctx) =>
        {
            try
            {
                var snapshot = SharedData.ApiTimings.GetSnapshot();
                
                var data = snapshot.Values
                    .Select(item => new
                    {
                        path = item.Path,
                        method = item.Method,
                        backend = item.Backend,
                        requestCount = item.RequestCount,
                        avgTotalTime = Math.Round(item.AvgTotalTime, 2),
                        avgBackendTime = Math.Round(item.AvgBackendTime, 2),
                        avgGatewayTime = Math.Round(item.AvgGatewayTime, 2),
                        minTotalTime = item.MinTotalTime == long.MaxValue ? 0 : item.MinTotalTime,
                        maxTotalTime = item.MaxTotalTime,
                        minBackendTime = item.MinBackendTime == long.MaxValue ? 0 : item.MinBackendTime,
                        maxBackendTime = item.MaxBackendTime,
                        totalTime = item.TotalTime,
                        backendTime = item.BackendTime,
                        statusCodeCounts = item.StatusCodeCounts,
                        lastRequestTime = item.LastRequestTime,
                        // 计算错误率（4xx + 5xx）
                        errorRate = item.RequestCount > 0 
                            ? Math.Round(item.StatusCodeCounts
                                .Where(kv => kv.Key >= 400)
                                .Sum(kv => kv.Value) * 100.0 / item.RequestCount, 2)
                            : 0
                    })
                    .ToList();
                
                // 计算汇总数据
                var totalRequests = data.Sum(x => x.requestCount);
                var totalApis = data.Count;
                var avgTotalTime = totalRequests > 0 
                    ? Math.Round(data.Sum(x => x.totalTime) / (double)totalRequests, 2) 
                    : 0;
                var avgBackendTime = totalRequests > 0 
                    ? Math.Round(data.Sum(x => x.backendTime) / (double)totalRequests, 2) 
                    : 0;
                var avgGatewayTime = avgTotalTime - avgBackendTime;
                
                // 按后端地址统计
                var backendStats = data
                    .Where(x => !string.IsNullOrEmpty(x.backend))
                    .GroupBy(x => x.backend)
                    .Select(g => new
                    {
                        backend = g.Key,
                        apiCount = g.Count(),
                        totalRequests = g.Sum(x => x.requestCount),
                        avgTotalTime = g.Sum(x => x.requestCount) > 0 
                            ? Math.Round(g.Sum(x => x.totalTime) / (double)g.Sum(x => x.requestCount), 2) 
                            : 0,
                        avgBackendTime = g.Sum(x => x.requestCount) > 0 
                            ? Math.Round(g.Sum(x => x.backendTime) / (double)g.Sum(x => x.requestCount), 2) 
                            : 0,
                        errorCount = g.Sum(x => x.statusCodeCounts.Where(kv => kv.Key >= 400).Sum(kv => kv.Value)),
                        errorRate = g.Sum(x => x.requestCount) > 0 
                            ? Math.Round(g.Sum(x => x.statusCodeCounts.Where(kv => kv.Key >= 400).Sum(kv => kv.Value)) * 100.0 / g.Sum(x => x.requestCount), 2) 
                            : 0
                    })
                    .OrderByDescending(x => x.errorRate)
                    .ThenByDescending(x => x.avgBackendTime)
                    .ToList();
                
                return Results.Json(new
                {
                    success = true,
                    data,
                    backendStats,
                    summary = new
                    {
                        totalApis,
                        totalRequests,
                        avgTotalTime,
                        avgBackendTime,
                        avgGatewayTime
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // 清除 API 耗时统计数据
        app.MapPost("/api/timing/clear", (HttpContext ctx) =>
        {
            try
            {
                SharedData.ApiTimings.Clear();
                return Results.Json(new { success = true, message = "API 耗时统计数据已清除" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // 重置流量统计数据
        app.MapPost("/api/traffic/reset", (HttpContext ctx) =>
        {
            try
            {
                SharedData.Traffic.Reset();
                return Results.Json(new { success = true, message = "流量统计数据已重置" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 仪表板数据 API ===============
        
        // 获取仪表板概览数据
        app.MapGet("/api/dashboard", (HttpContext ctx, 
            IAccessControlService accessControlService,
            IStatisticService statisticService,
            IProtectService protectService,
            IABTestService abTestService) =>
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var uptime = DateTime.Now - process.StartTime;
                var connectionStats = accessControlService.GetConnectionStats();
                
                // 获取配置选项
                var acOptions = accessControlService.GetOptions();
                var protectOptions = protectService.GetOptions();
                var statisticOptions = statisticService.GetOption();
                
                // 获取流量统计
                var trafficSnapshot = SharedData.Traffic.GetSnapshot();
                
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

                return Results.Json(new
                {
                    success = true,
                    system = new
                    {
                        uptime = uptimeStr,
                        memory = process.WorkingSet64 / (1024 * 1024),
                        totalConnections = connectionStats.TotalConnections,
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
                        post = protectService.GetPostRegexList()
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取流量统计数据
        app.MapGet("/api/traffic/stats", (HttpContext ctx) =>
        {
            try
            {
                var trafficSnapshot = SharedData.Traffic.GetSnapshot();
                return Results.Json(new
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 安全态势统计数据
        app.MapGet("/api/security/stats", (HttpContext ctx) =>
        {
            try
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
                
                return Results.Json(new
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // 重置安全态势统计数据
        app.MapPost("/api/security/reset", (HttpContext ctx) =>
        {
            try
            {
                SharedData.Security.Reset();
                return Results.Json(new { success = true, message = "安全态势统计数据已重置" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/status", (HttpContext ctx) =>
        {
            return Results.Json(new
            {
                status = "running",
                pid = Environment.ProcessId,
                uptime = DateTime.Now - Process.GetCurrentProcess().StartTime,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/info", (HttpContext ctx) =>
        {
            var process = Process.GetCurrentProcess();
            return Results.Json(new
            {
                pid = process.Id,
                name = process.ProcessName,
                startTime = process.StartTime,
                memoryMB = process.WorkingSet64 / (1024 * 1024),
                threads = process.Threads.Count
            });
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/config", (HttpContext ctx, IConfiguration config) =>
        {
            var configDict = new Dictionary<string, object?>();
            foreach (var section in config.GetChildren())
            {
                if (AllowedConfigKeys.Contains(section.Key))
                {
                    configDict[section.Key] = GetSectionValue(section);
                }
            }
            
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(configDict);
            
            return Results.Text(yaml, "text/yaml", Encoding.UTF8);
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/stop", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            // 停止应用会触发插件系统停止（包括 AnalysisPlugin）
            lifetime.StopApplication();
            return Results.Json(new { message = "服务正在停止..." });
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/reload", (HttpContext ctx, IConfiguration config) =>
        {
            if (config is IConfigurationRoot configRoot)
            {
                configRoot.Reload();
                return Results.Json(new { message = "配置已重新加载", timestamp = DateTime.Now });
            }
            return Results.Json(new { message = "配置重载失败：不支持的配置类型" }, statusCode: 500);
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/statistics", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var queryIp = ctx.Request.Query["ip"].FirstOrDefault();
            var isFilterByIp = !string.IsNullOrEmpty(queryIp);

            var connectionStats = accessControlService.GetConnectionStats();

            // 如果指定了IP，只返回该IP的连接统计
            if (isFilterByIp)
            {
                connectionStats = new ConnectionStats
                {
                    TotalConnections = connectionStats.TotalConnections,
                    ConnectionsPerIp = connectionStats.ConnectionsPerIp
                        .Where(kv => kv.Key == queryIp)
                        .ToDictionary(kv => kv.Key, kv => kv.Value),
                    ConnectionsPerDestination = connectionStats.ConnectionsPerDestination,
                    ConnectionsPerPath = connectionStats.ConnectionsPerPath
                };
            }

            // 获取客户端统计数据
            var clientStatsSnapshot = SharedData.ClientStas.GetSnapshot();
            
            if (isFilterByIp)
            {
                clientStatsSnapshot = clientStatsSnapshot
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var clientStats = clientStatsSnapshot
                .Select(kv => new
                {
                    ip = kv.Key,
                    totalCount = kv.Value.CountTime.Count,
                    totalTime = kv.Value.CountTime.UseTime,
                    avgTime = kv.Value.CountTime.Average,
                    urlStats = kv.Value.UrlCostTime.Select(u => new
                    {
                        path = u.Key,
                        count = u.Value.Count,
                        totalTime = u.Value.UseTime,
                        avgTime = u.Value.Average
                    }).OrderByDescending(x => x.count).Take(10)
                })
                .OrderByDescending(x => x.totalCount)
                .Take(isFilterByIp ? int.MaxValue : 100)
                .ToList();

            // 如果指定了IP，不返回请求路径统计和目标服务器统计
            List<object>? requestStats = null;
            List<object>? destinationStats = null;

            if (!isFilterByIp)
            {
                // 获取请求路径统计数据
                requestStats = SharedData.ReqStas.GetSnapshot()
                    .Select(kv => new
                    {
                        path = kv.Key,
                        totalCount = kv.Value.CountTime.Count,
                        totalTime = kv.Value.CountTime.UseTime,
                        avgTime = kv.Value.CountTime.Average
                    })
                    .OrderByDescending(x => x.totalCount)
                    .Take(50)
                    .Cast<object>()
                    .ToList();

                // 获取目标服务器统计数据
                destinationStats = SharedData.DestStas.GetSnapshot()
                    .Select(kv => new
                    {
                        destination = kv.Key,
                        totalCount = kv.Value.CountTime.Count,
                        totalTime = kv.Value.CountTime.UseTime,
                        avgTime = kv.Value.CountTime.Average
                    })
                    .OrderByDescending(x => x.totalCount)
                    .Cast<object>()
                    .ToList();
            }

            // 获取被封禁的IP（如果指定了IP，只返回该IP是否被封禁）
            var blockedIpsSnapshot = SharedData.ClientFb.GetSnapshot();
            
            if (isFilterByIp)
            {
                blockedIpsSnapshot = blockedIpsSnapshot
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var blockedIps = blockedIpsSnapshot
                .Select(kv => new
                {
                    ip = kv.Key,
                    reason = kv.Value
                })
                .ToList();

            // 获取CC限制统计（如果指定了IP，只返回该IP相关的）
            var ccLimitStatsSnapshot = SharedData.LimitCcStas.GetSnapshot();
            
            if (isFilterByIp)
            {
                ccLimitStatsSnapshot = ccLimitStatsSnapshot
                    .Where(kv => kv.Key.Contains(queryIp ?? ""))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var ccLimitStats = ccLimitStatsSnapshot
                .Select(kv => new
                {
                    key = kv.Key,
                    count = kv.Value
                })
                .OrderByDescending(x => x.count)
                .Take(isFilterByIp ? int.MaxValue : 50)
                .ToList();

            // 获取客户端访问次数（如果指定了IP，只返回该IP的）
            var clientVisitsSnapshot = SharedData.NewClientVisits.GetSnapshot();
            
            if (isFilterByIp)
            {
                clientVisitsSnapshot = clientVisitsSnapshot
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var clientVisits = clientVisitsSnapshot
                .Select(kv => new
                {
                    ip = kv.Key,
                    visits = kv.Value
                })
                .OrderByDescending(x => x.visits)
                .Take(isFilterByIp ? int.MaxValue : 50)
                .ToList();

            // 获取客户端最后访问时间（从 ClientStas 的 LastAccessTime 字段获取）
            var clientStasForLastAccess = SharedData.ClientStas.GetSnapshot();
            
            if (isFilterByIp)
            {
                clientStasForLastAccess = clientStasForLastAccess
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var clientLastAccess = clientStasForLastAccess
                .Select(kv => new
                {
                    ip = kv.Key,
                    lastAccessTime = DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.LastAccessTime).LocalDateTime
                })
                .OrderByDescending(x => x.lastAccessTime)
                .Take(isFilterByIp ? int.MaxValue : 50)
                .ToList();

            var result = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now,
                ["summary"] = new
                {
                    totalClients = isFilterByIp ? (clientStats.Count > 0 ? 1 : 0) : SharedData.ClientStas.Count,
                    totalBlockedIps = isFilterByIp ? (blockedIps.Count > 0 ? 1 : 0) : SharedData.ClientFb.Count,
                    totalConnections = connectionStats.TotalConnections,
                    filteredIp = isFilterByIp ? queryIp : null
                },
                ["connections"] = connectionStats,
                ["clientStats"] = clientStats,
                ["blockedIps"] = blockedIps,
                ["ccLimitStats"] = ccLimitStats,
                ["clientVisits"] = clientVisits,
                ["clientLastAccess"] = clientLastAccess
            };

            // 只有在未指定IP时才添加这些字段
            if (!isFilterByIp)
            {
                result["requestStats"] = requestStats;
                result["destinationStats"] = destinationStats;
            }

            return Results.Json(result);
        }).RequireHost($"*:{controlPort}");

        // =============== 功能状态切换 API ===============
        
        // 切换 IP 访问控制状态
        app.MapPost("/api/feature/ip-control/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().IpControl.Enabled;
                accessControlService.SetIpControlEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "ip-control",
                    enabled = enabled,
                    message = enabled ? "IP 访问控制已启用" : "IP 访问控制已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换地理位置访问控制状态
        app.MapPost("/api/feature/geo-control/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().GeoControl.Enabled;
                accessControlService.SetGeoControlEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "geo-control",
                    enabled = enabled,
                    message = enabled ? "地理位置访问控制已启用" : "地理位置访问控制已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换 WAF Args 检测状态
        app.MapPost("/api/feature/waf-args/toggle", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !protectService.GetOptions().OpenArgsCheck;
                protectService.SetArgsCheckEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "waf-args",
                    enabled = enabled,
                    message = enabled ? "WAF Args 检测已启用" : "WAF Args 检测已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换 WAF Post 检测状态
        app.MapPost("/api/feature/waf-post/toggle", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !protectService.GetOptions().OpenPostCheck;
                protectService.SetPostCheckEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "waf-post",
                    enabled = enabled,
                    message = enabled ? "WAF Post 检测已启用" : "WAF Post 检测已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 动态访问控制管理 API ===============
        
        // 获取白名单列表
        app.MapGet("/api/ac/whitelist", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var whitelist = accessControlService.GetWhitelist();
            return Results.Json(new
            {
                success = true,
                count = whitelist.Count,
                whitelist,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 IP 到白名单
        app.MapPost("/api/ac/whitelist/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.AddWhitelist(request.IpOrCidr);
                if (result)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功添加白名单: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：IP 格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从白名单移除 IP
        app.MapPost("/api/ac/whitelist/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.RemoveWhitelist(request.IpOrCidr);
                if (result)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功从白名单移除: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在动态白名单中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取黑名单列表
        app.MapGet("/api/ac/blacklist", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var blacklist = accessControlService.GetBlacklist();
            return Results.Json(new
            {
                success = true,
                count = blacklist.Count,
                blacklist = blacklist,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 IP 到黑名单
        app.MapPost("/api/ac/blacklist/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.AddBlacklist(request.IpOrCidr);
                if (result)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功添加黑名单: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：IP 格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从黑名单移除 IP
        app.MapPost("/api/ac/blacklist/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.RemoveBlacklist(request.IpOrCidr);
                if (result)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功从黑名单移除: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在动态黑名单中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 被封禁 IP 管理 API ===============
        
        // 获取被封禁的 IP 列表
        app.MapGet("/api/blocked-ips", (HttpContext ctx) =>
        {
            var blockedIps = SharedData.ClientFb.GetValidItemsWithExpiry()
                .Select(x => new
                {
                    ip = x.Key,
                    type = "blocked",
                    reason = x.Value,
                    remainingSeconds = x.RemainingTime?.TotalSeconds,
                    expiresAt = SharedData.ClientFb.GetExpiration(x.Key)
                })
                .Concat(SharedData.CaptchaPending.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "captcha",
                        reason = $"验证码待验证: {x.Value.RuleName}",
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        expiresAt = SharedData.CaptchaPending.GetExpiration(x.Key)
                    }))
                .Concat(SharedData.ClientThrottled.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "throttled",
                        reason = $"带宽限速: {x.Value.EveryCapacity / 1024}KB/s",
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        expiresAt = SharedData.ClientThrottled.GetExpiration(x.Key)
                    }))
                .OrderBy(x => x.ip)
                .ToList();

            return Results.Json(new
            {
                success = true,
                count = blockedIps.Count,
                blockedIps = blockedIps,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 手动封禁 IP
        app.MapPost("/api/blocked-ips/add", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<BlockIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var duration = request.Duration ?? TimeSpan.FromMinutes(10);
                var reason = request.Reason ?? "手动封禁";
                
                SharedData.ClientFb.Set(request.Ip.Trim(), reason, duration);

                return Results.Json(new
                {
                    success = true,
                    message = $"已封禁 IP: {request.Ip}",
                    ip = request.Ip,
                    reason = reason,
                    duration = duration.ToString(),
                    expiresAt = DateTime.Now.Add(duration),
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"封禁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 解封 IP
        app.MapPost("/api/blocked-ips/remove", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UnblockIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var removed = SharedData.ClientFb.Remove(ip);
                removed |= SharedData.CaptchaPending.Remove(ip);
                removed |= SharedData.ClientThrottled.Remove(ip);
                
                if (removed)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已解封 IP: {ip}",
                        ip = ip,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "IP 不在封禁列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"解封失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 清空所有封禁的 IP
        app.MapPost("/api/blocked-ips/clear", (HttpContext ctx) =>
        {
            var count = SharedData.ClientFb.Count + SharedData.CaptchaPending.Count + SharedData.ClientThrottled.Count;
            SharedData.ClientFb.Clear();
            SharedData.CaptchaPending.Clear();
            SharedData.ClientThrottled.Clear();
            return Results.Json(new
            {
                success = true,
                message = $"已清空 {count} 个被封禁的 IP",
                clearedCount = count,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // =============== WAF 规则管理 API ===============
        
        // 获取 WAF 规则列表
        app.MapGet("/api/waf/rules", (HttpContext ctx, IProtectService protectService) =>
        {
            var options = protectService.GetOptions();
            return Results.Json(new
            {
                success = true,
                openArgsCheck = options.OpenArgsCheck,
                openPostCheck = options.OpenPostCheck,
                argsRules = protectService.GetArgsRegexList(),
                postRules = protectService.GetPostRegexList(),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 WAF Args 规则
        app.MapPost("/api/waf/args/add", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.AddArgsRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 Args WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：正则格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 WAF Args 规则
        app.MapPost("/api/waf/args/remove", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.RemoveArgsRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 Args WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加 WAF Post 规则
        app.MapPost("/api/waf/post/add", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.AddPostRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 Post WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：正则格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 WAF Post 规则
        app.MapPost("/api/waf/post/remove", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.RemovePostRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 Post WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== CC 防护规则管理 API ===============
        
        // 获取 CC 规则列表
        app.MapGet("/api/cc/rules", (HttpContext ctx, IStatisticService statisticService) =>
        {
            var options = statisticService.GetOption();
            return Results.Json(new
            {
                success = true,
                rules = statisticService.GetLimitCcRules().Select(r => new
                {
                    path = r.Path,
                    period = r.Period,
                    limitNum = r.LimitNum,
                    fbTime = r.FbTime.ToString()
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 CC 规则
        app.MapPost("/api/cc/rules/add", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                var rule = new LimitCcOption
                {
                    Path = request.Path,
                    Period = request.Period ?? 60,
                    LimitNum = request.LimitNum ?? 100,
                    FbTime = request.FbTime ?? TimeSpan.FromMinutes(5)
                };

                if (statisticService.AddLimitCcRule(rule))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 CC 规则: {request.Path}",
                        rule = new { rule.Path, rule.Period, rule.LimitNum, fbTime = rule.FbTime.ToString() },
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：规则已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 CC 规则
        app.MapPost("/api/cc/rules/remove", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (statisticService.RemoveLimitCcRule(request.Path))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 CC 规则: {request.Path}",
                        path = request.Path,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 高级 CC 规则管理 API ===============
        
        // 获取所有高级 CC 规则
        app.MapGet("/api/cc/advanced", (HttpContext ctx, IStatisticService statisticService) =>
        {
            var rules = statisticService.GetAdvancedCcRules();
            return Results.Json(new
            {
                success = true,
                count = rules.Count,
                rules = rules.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    enabled = r.Enabled,
                    type = r.Type.ToString(),
                    conditions = r.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = r.Period,
                    threshold = r.Threshold,
                    action = r.Action.ToString(),
                    actionSeconds = r.ActionSeconds,
                    priority = r.Priority,
                    createdAt = r.CreatedAt
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 按类型获取高级 CC 规则
        app.MapGet("/api/cc/advanced/type/{type}", (HttpContext ctx, IStatisticService statisticService, string type) =>
        {
            if (!Enum.TryParse<CcRuleType>(type, true, out var ruleType))
            {
                return Results.Json(new { success = false, message = "无效的规则类型" }, statusCode: 400);
            }
            
            var rules = statisticService.GetAdvancedCcRulesByType(ruleType);
            return Results.Json(new
            {
                success = true,
                type = ruleType.ToString(),
                count = rules.Count,
                rules = rules.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    enabled = r.Enabled,
                    type = r.Type.ToString(),
                    conditions = r.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = r.Period,
                    threshold = r.Threshold,
                    action = r.Action.ToString(),
                    actionSeconds = r.ActionSeconds,
                    priority = r.Priority,
                    createdAt = r.CreatedAt
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取单个高级 CC 规则
        app.MapGet("/api/cc/advanced/{ruleId}", (HttpContext ctx, IStatisticService statisticService, string ruleId) =>
        {
            var rule = statisticService.GetAdvancedCcRule(ruleId);
            if (rule == null)
            {
                return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            }
            
            return Results.Json(new
            {
                success = true,
                rule = new
                {
                    id = rule.Id,
                    name = rule.Name,
                    enabled = rule.Enabled,
                    type = rule.Type.ToString(),
                    conditions = rule.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = rule.Period,
                    threshold = rule.Threshold,
                    action = rule.Action.ToString(),
                    actionSeconds = rule.ActionSeconds,
                    priority = rule.Priority,
                    createdAt = rule.CreatedAt
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加高级 CC 规则
        app.MapPost("/api/cc/advanced/add", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.Json(new { success = false, message = "规则名称不能为空" }, statusCode: 400);
                }

                var rule = new AdvancedCcRule
                {
                    Name = request.Name,
                    Enabled = request.Enabled ?? true,
                    Type = Enum.TryParse<CcRuleType>(request.Type, true, out var t) ? t : CcRuleType.FrequentAccess,
                    Period = request.Period ?? 10,
                    Threshold = request.Threshold ?? 100,
                    Action = Enum.TryParse<CcAction>(request.Action, true, out var a) ? a : CcAction.Captcha,
                    ActionSeconds = request.ActionSeconds ?? 600,
                    Priority = request.Priority ?? 100,
                    Conditions = request.Conditions?.Select(c => new CcCondition
                    {
                        Target = Enum.TryParse<CcMatchTarget>(c.Target, true, out var ct) ? ct : CcMatchTarget.UrlPath,
                        Operator = Enum.TryParse<CcMatchOperator>(c.Operator, true, out var co) ? co : CcMatchOperator.Equal,
                        Values = c.Values ?? []
                    }).ToList() ?? []
                };

                if (statisticService.AddAdvancedCcRule(rule))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加高级 CC 规则: {rule.Name}",
                        ruleId = rule.Id,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新高级 CC 规则
        app.MapPost("/api/cc/advanced/update", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Id))
                {
                    return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
                }

                var existingRule = statisticService.GetAdvancedCcRule(request.Id);
                if (existingRule == null)
                {
                    return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
                }

                var rule = new AdvancedCcRule
                {
                    Id = request.Id,
                    Name = request.Name ?? existingRule.Name,
                    Enabled = request.Enabled ?? existingRule.Enabled,
                    Type = request.Type != null && Enum.TryParse<CcRuleType>(request.Type, true, out var t) ? t : existingRule.Type,
                    Period = request.Period ?? existingRule.Period,
                    Threshold = request.Threshold ?? existingRule.Threshold,
                    Action = request.Action != null && Enum.TryParse<CcAction>(request.Action, true, out var a) ? a : existingRule.Action,
                    ActionSeconds = request.ActionSeconds ?? existingRule.ActionSeconds,
                    Priority = request.Priority ?? existingRule.Priority,
                    Conditions = request.Conditions?.Select(c => new CcCondition
                    {
                        Target = Enum.TryParse<CcMatchTarget>(c.Target, true, out var ct) ? ct : CcMatchTarget.UrlPath,
                        Operator = Enum.TryParse<CcMatchOperator>(c.Operator, true, out var co) ? co : CcMatchOperator.Equal,
                        Values = c.Values ?? []
                    }).ToList() ?? existingRule.Conditions
                };

                if (statisticService.UpdateAdvancedCcRule(rule))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已更新高级 CC 规则: {rule.Name}",
                        ruleId = rule.Id,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "更新失败" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除高级 CC 规则
        app.MapPost("/api/cc/advanced/remove", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.RuleId))
                {
                    return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
                }

                if (statisticService.RemoveAdvancedCcRule(request.RuleId))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = "已删除高级 CC 规则",
                        ruleId = request.RuleId,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "删除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 启用/禁用高级 CC 规则
        app.MapPost("/api/cc/advanced/toggle", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.RuleId))
                {
                    return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
                }

                var rule = statisticService.GetAdvancedCcRule(request.RuleId);
                var newState = request.Enabled ?? !(rule?.Enabled ?? false);

                if (statisticService.ToggleAdvancedCcRule(request.RuleId, newState))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = newState ? "已启用规则" : "已禁用规则",
                        ruleId = request.RuleId,
                        enabled = newState,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "操作失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取可用的匹配目标、操作符、动作等枚举值
        app.MapGet("/api/cc/advanced/enums", (HttpContext ctx) =>
        {
            return Results.Json(new
            {
                success = true,
                matchTargets = Enum.GetNames<CcMatchTarget>().Select(n => new { name = n, value = n }),
                matchOperators = Enum.GetNames<CcMatchOperator>().Select(n => new { name = n, value = n }),
                ruleTypes = Enum.GetNames<CcRuleType>().Select(n => new { name = n, value = n }),
                actions = Enum.GetNames<CcAction>().Select(n => new { name = n, value = n }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // =============== 地理位置黑名单管理 API ===============
        
        // 获取地理位置黑名单
        app.MapGet("/api/geo/deny-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetDenyCountries();
            return Results.Json(new
            {
                success = true,
                count = countries.Count,
                denyCountries = countries,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加国家到地理位置黑名单
        app.MapPost("/api/geo/deny-countries/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddDenyCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加禁止访问国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从地理位置黑名单移除国家
        app.MapPost("/api/geo/deny-countries/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveDenyCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从禁止列表移除国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态黑名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取禁止访问的省份列表
        app.MapGet("/api/geo/deny-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetDenyRegions();
            return Results.Json(new
            {
                success = true,
                count = regions.Count,
                denyRegions = regions,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加省份到禁止列表
        app.MapPost("/api/geo/deny-regions/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddDenyRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加禁止访问省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从禁止列表移除省份
        app.MapPost("/api/geo/deny-regions/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveDenyRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从禁止列表移除省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态黑名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取允许访问的国家列表
        app.MapGet("/api/geo/allow-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetAllowCountries();
            return Results.Json(new
            {
                success = true,
                count = countries.Count,
                allowCountries = countries,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加国家到允许列表
        app.MapPost("/api/geo/allow-countries/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddAllowCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加允许访问国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从允许列表移除国家
        app.MapPost("/api/geo/allow-countries/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveAllowCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从允许列表移除国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态白名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取允许访问的省份列表
        app.MapGet("/api/geo/allow-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetAllowRegions();
            return Results.Json(new
            {
                success = true,
                count = regions.Count,
                allowRegions = regions,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加省份到允许列表
        app.MapPost("/api/geo/allow-regions/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddAllowRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加允许访问省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从允许列表移除省份
        app.MapPost("/api/geo/allow-regions/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveAllowRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从允许列表移除省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态白名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 带宽限速管理 API ===============
        
        // 获取带宽限速配置
        app.MapGet("/api/throttle/config", (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            var config = speedLimitService.GetThrottleConfig();
            return Results.Json(new
            {
                success = true,
                global = config.Global,
                pathLimits = config.PathLimits,
                ipLimits = config.IpLimits,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 设置 IP 带宽限速
        app.MapPost("/api/throttle/ip/add", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                if (request.LimitKbps <= 0)
                {
                    return Results.Json(new { success = false, message = "限速值必须大于 0" }, statusCode: 400);
                }

                speedLimitService.SetIpThrottle(request.Ip, request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = $"已设置 IP {request.Ip} 带宽限速: {request.LimitKbps} KB/s",
                    ip = request.Ip,
                    limitKbps = request.LimitKbps,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 IP 带宽限速
        app.MapPost("/api/throttle/ip/remove", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                if (speedLimitService.RemoveIpThrottle(request.Ip))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 IP {request.Ip} 的带宽限速",
                        ip = request.Ip,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在限速列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 设置路径带宽限速
        app.MapPost("/api/throttle/path/add", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddPathThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (request.LimitKbps <= 0)
                {
                    return Results.Json(new { success = false, message = "限速值必须大于 0" }, statusCode: 400);
                }

                speedLimitService.SetPathThrottle(request.Path, request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = $"已设置路径 {request.Path} 带宽限速: {request.LimitKbps} KB/s",
                    path = request.Path,
                    limitKbps = request.LimitKbps,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除路径带宽限速
        app.MapPost("/api/throttle/path/remove", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemovePathThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (speedLimitService.RemovePathThrottle(request.Path))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除路径 {request.Path} 的带宽限速",
                        path = request.Path,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：路径不在限速列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== A/B 测试管理 API ===============
        
        // 获取所有 A/B 测试配置
        app.MapGet("/api/abtest/configs", (HttpContext ctx, IABTestService abTestService) =>
        {
            var configs = abTestService.GetAllConfigs();
            return Results.Json(new
            {
                success = true,
                count = configs.Count,
                configs = configs.Select(c => new
                {
                    testId = c.Key,
                    name = c.Value.Name,
                    enabled = c.Value.Enabled,
                    mode = c.Value.Mode.ToString(),
                    cookieName = c.Value.CookieName,
                    variants = c.Value.Variants,
                    variantTargets = c.Value.VariantTargets,
                    matchPaths = c.Value.MatchPaths,
                    excludePaths = c.Value.ExcludePaths
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取单个 A/B 测试配置
        app.MapGet("/api/abtest/configs/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            return Results.Json(new
            {
                success = true,
                testId = testId,
                config = new
                {
                    name = config.Name,
                    enabled = config.Enabled,
                    mode = config.Mode.ToString(),
                    cookieName = config.CookieName,
                    cookieExpireDays = config.CookieExpireDays,
                    variants = config.Variants,
                    variantTargets = config.VariantTargets,
                    matchPaths = config.MatchPaths,
                    excludePaths = config.ExcludePaths
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 创建或更新 A/B 测试配置
        app.MapPost("/api/abtest/configs", async (HttpContext ctx, IABTestService abTestService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CreateABTestRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.TestId))
                {
                    return Results.Json(new { success = false, message = "测试 ID 不能为空" }, statusCode: 400);
                }

                if (request.Variants == null || request.Variants.Count < 2)
                {
                    return Results.Json(new { success = false, message = "至少需要 2 个变体" }, statusCode: 400);
                }

                // 验证权重总和
                var totalWeight = request.Variants.Values.Sum();
                if (totalWeight <= 0)
                {
                    return Results.Json(new { success = false, message = "权重总和必须大于 0" }, statusCode: 400);
                }

                var config = new ABTestConfig
                {
                    Name = request.Name ?? request.TestId,
                    Enabled = request.Enabled ?? true,
                    Mode = Enum.TryParse<ABTestMode>(request.Mode, true, out var mode) ? mode : ABTestMode.CookieSticky,
                    CookieName = request.CookieName ?? $"ab_{request.TestId}",
                    CookieExpireDays = request.CookieExpireDays ?? 30,
                    Variants = request.Variants,
                    VariantTargets = request.VariantTargets ?? new Dictionary<string, string>(),
                    MatchPaths = request.MatchPaths ?? new List<string>(),
                    ExcludePaths = request.ExcludePaths ?? new List<string>()
                };

                abTestService.SetConfig(request.TestId, config);

                return Results.Json(new
                {
                    success = true,
                    message = $"已创建/更新 A/B 测试: {request.TestId}",
                    testId = request.TestId,
                    config = new
                    {
                        name = config.Name,
                        enabled = config.Enabled,
                        mode = config.Mode.ToString(),
                        variants = config.Variants,
                        totalWeight = totalWeight
                    },
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"创建失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除 A/B 测试配置
        app.MapDelete("/api/abtest/configs/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            if (abTestService.RemoveConfig(testId))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已删除 A/B 测试: {testId}",
                    testId = testId,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }
        }).RequireHost($"*:{controlPort}");

        // 启用/禁用 A/B 测试
        app.MapPost("/api/abtest/configs/{testId}/toggle", async (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            var request = await ctx.Request.ReadFromJsonAsync<ToggleABTestRequest>();
            config.Enabled = request?.Enabled ?? !config.Enabled;
            abTestService.SetConfig(testId, config);

            return Results.Json(new
            {
                success = true,
                message = config.Enabled ? "已启用 A/B 测试" : "已禁用 A/B 测试",
                testId = testId,
                enabled = config.Enabled,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取 A/B 测试统计
        app.MapGet("/api/abtest/stats/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var stats = abTestService.GetStats(testId);
            var config = abTestService.GetConfig(testId);
            
            if (stats == null || config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            // 计算各变体的实际百分比
            var variantPercentages = stats.VariantHits.ToDictionary(
                v => v.Key,
                v => stats.TotalRequests > 0 ? Math.Round((double)v.Value / stats.TotalRequests * 100, 2) : 0
            );

            return Results.Json(new
            {
                success = true,
                testId = testId,
                stats = new
                {
                    totalRequests = stats.TotalRequests,
                    variantHits = stats.VariantHits,
                    variantPercentages = variantPercentages,
                    configuredWeights = config.Variants,
                    startTime = stats.StartTime,
                    lastRequestTime = stats.LastRequestTime
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 重置 A/B 测试统计
        app.MapPost("/api/abtest/stats/{testId}/reset", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            abTestService.ResetStats(testId);

            return Results.Json(new
            {
                success = true,
                message = "已重置 A/B 测试统计",
                testId = testId,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");
        
        return app;
    }

    private static object? GetSectionValue(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        
        if (children.Count == 0)
        {
            return section.Value;
        }
        
        // 检查是否是数组（子项的 Key 都是数字）
        if (children.All(c => int.TryParse(c.Key, out _)))
        {
            var list = new List<object?>();
            foreach (var child in children.OrderBy(c => int.Parse(c.Key)))
            {
                list.Add(GetSectionValue(child));
            }
            return list;
        }
        
        // 否则是对象
        var dict = new Dictionary<string, object?>();
        foreach (var child in children)
        {
            dict[child.Key] = GetSectionValue(child);
        }
        return dict;
    }
}

// =============== 请求模型 ===============

public class AddIpRequest
{
    public string IpOrCidr { get; set; } = "";
}

public class RemoveIpRequest
{
    public string IpOrCidr { get; set; } = "";
}

public class BlockIpRequest
{
    public string Ip { get; set; } = "";
    public string? Reason { get; set; }
    public TimeSpan? Duration { get; set; }
}

public class UnblockIpRequest
{
    public string Ip { get; set; } = "";
}

public class AddWafRuleRequest
{
    public string Pattern { get; set; } = "";
}

public class RemoveWafRuleRequest
{
    public string Pattern { get; set; } = "";
}

public class AddCcRuleRequest
{
    public string Path { get; set; } = "";
    public int? Period { get; set; }
    public int? LimitNum { get; set; }
    public TimeSpan? FbTime { get; set; }
}

public class RemoveCcRuleRequest
{
    public string Path { get; set; } = "";
}

// =============== 高级 CC 规则请求模型 ===============

public class CcConditionRequest
{
    public string Target { get; set; } = "UrlPath";
    public string Operator { get; set; } = "Equal";
    public List<string>? Values { get; set; }
}

public class AddAdvancedCcRuleRequest
{
    public string Name { get; set; } = "";
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public List<CcConditionRequest>? Conditions { get; set; }
    public int? Period { get; set; }
    public int? Threshold { get; set; }
    public string? Action { get; set; }
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class UpdateAdvancedCcRuleRequest
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public List<CcConditionRequest>? Conditions { get; set; }
    public int? Period { get; set; }
    public int? Threshold { get; set; }
    public string? Action { get; set; }
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class RemoveAdvancedCcRuleRequest
{
    public string RuleId { get; set; } = "";
}

public class ToggleAdvancedCcRuleRequest
{
    public string RuleId { get; set; } = "";
    public bool? Enabled { get; set; }
}

public class AddCountryRequest
{
    public string Country { get; set; } = "";
}

public class RemoveCountryRequest
{
    public string Country { get; set; } = "";
}

public class AddRegionRequest
{
    public string Region { get; set; } = "";
}

public class RemoveRegionRequest
{
    public string Region { get; set; } = "";
}

public class AddIpThrottleRequest
{
    public string Ip { get; set; } = "";
    public int LimitKbps { get; set; }
}

public class RemoveIpThrottleRequest
{
    public string Ip { get; set; } = "";
}

public class AddPathThrottleRequest
{
    public string Path { get; set; } = "";
    public int LimitKbps { get; set; }
}

public class RemovePathThrottleRequest
{
    public string Path { get; set; } = "";
}

public class CreateABTestRequest
{
    public string TestId { get; set; } = "";
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Mode { get; set; } // Random, CookieSticky, IpHash, UserIdHash
    public string? CookieName { get; set; }
    public int? CookieExpireDays { get; set; }
    public Dictionary<string, int> Variants { get; set; } = new(); // { "A": 70, "B": 30 }
    public Dictionary<string, string>? VariantTargets { get; set; } // { "A": "destination-a", "B": "destination-b" }
    public List<string>? MatchPaths { get; set; }
    public List<string>? ExcludePaths { get; set; }
}

public class ToggleABTestRequest
{
    public bool? Enabled { get; set; }
}

public class ToggleFeatureRequest
{
    public bool? Enabled { get; set; }
}
