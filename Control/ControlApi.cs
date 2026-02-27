using System.Diagnostics;
using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Config;
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
                        originalHost = item.OriginalHost,
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
                SharedData.GeoTraffic.Reset();
                return Results.Json(new { success = true, message = "流量统计数据已重置" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 地理位置流量统计 API ===============

        app.MapGet("/api/geo-traffic/stats", (HttpContext ctx) =>
        {
            try
            {
                var snapshot = SharedData.GeoTraffic.GetSnapshot();
                return Results.Json(new
                {
                    success = true,
                    countryVisits = snapshot.CountryVisits
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    countryIntercepts = snapshot.CountryIntercepts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    regionVisits = snapshot.RegionVisits
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    regionIntercepts = snapshot.RegionIntercepts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                });
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

        // =============== 配置文件管理 API ===============

        // 获取配置文件内容（优先返回草稿内容，同时返回原始文件内容）
        app.MapGet("/api/config/file", (HttpContext ctx) =>
        {
            try
            {
                var filePath = SharedData.ConfigFilePath;
                if (!File.Exists(filePath))
                {
                    return Results.Json(new { success = false, message = $"配置文件不存在: {filePath}" }, statusCode: 404);
                }

                var originalContent = File.ReadAllText(filePath, Encoding.UTF8);
                var fileName = Path.GetFileName(filePath);
                var format = filePath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase) ? "ly" : "yaml";

                // 检查是否存在草稿文件（仅在已加载草稿配置时才返回草稿内容）
                var dir = Path.GetDirectoryName(filePath) ?? ".";
                var ext = Path.GetExtension(filePath);
                var draftPath = Path.Combine(dir, $".lywaf.draft{ext}");
                var draftExists = File.Exists(draftPath);
                string? draftContent = null;
                if (draftExists && SharedData.UseDraftConfig)
                {
                    draftContent = File.ReadAllText(draftPath, Encoding.UTF8);
                }

                return Results.Json(new
                {
                    success = true,
                    fileName,
                    format,
                    content = originalContent,
                    draftContent,
                    hasDraft = draftExists,
                    draftLoaded = SharedData.UseDraftConfig && draftExists
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"读取配置文件失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 保存配置文件内容（保存到草稿文件，不覆盖原始配置）
        app.MapPost("/api/config/file", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<SaveConfigRequest>();
                if (request == null || string.IsNullOrEmpty(request.Content))
                {
                    return Results.Json(new { success = false, message = "请求内容为空" }, statusCode: 400);
                }

                var originalPath = SharedData.ConfigFilePath;
                var isLy = originalPath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase);

                // 如果是 .ly 文件，先校验语法
                if (isLy)
                {
                    try
                    {
                        LyConfigParser.Parse(request.Content);
                    }
                    catch (LyConfigException ex)
                    {
                        return Results.Json(new { success = false, message = $"语法错误: {ex.Message}" }, statusCode: 400);
                    }
                }

                // 保存到草稿文件（不覆盖原始配置文件）
                var dir = Path.GetDirectoryName(originalPath) ?? ".";
                var ext = Path.GetExtension(originalPath);
                var draftPath = Path.Combine(dir, $".lywaf.draft{ext}");

                await File.WriteAllTextAsync(draftPath, request.Content, Encoding.UTF8);

                var message = $"草稿已保存到 {Path.GetFileName(draftPath)}";

                // 如果需要重载配置，设置标志后 Reload
                // LyConfigProvider / DraftAwareYamlProvider 检查此标志决定是否读取草稿
                if (request.Reload)
                {
                    if (config is IConfigurationRoot configRoot)
                    {
                        SharedData.UseDraftConfig = true;
                        configRoot.Reload();
                        message = "草稿已保存，配置已重新加载（端口监听等信息需重启服务才能生效）";
                    }
                }

                return Results.Json(new
                {
                    success = true,
                    message,
                    draftFile = Path.GetFileName(draftPath)
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 将 .ly 内容转换为 YAML 预览
        app.MapPost("/api/config/convert", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ConvertConfigRequest>();
                if (request == null || string.IsNullOrEmpty(request.Content))
                {
                    return Results.Json(new { success = false, message = "请求内容为空" }, statusCode: 400);
                }

                // 收集环境变量用于变量替换
                var variables = new Dictionary<string, string>();
                foreach (var key in Environment.GetEnvironmentVariables().Keys)
                {
                    var keyStr = key.ToString()!;
                    variables[keyStr] = Environment.GetEnvironmentVariable(keyStr) ?? "";
                }

                var yaml = LyToAppSettingsConverter.Convert(request.Content, variables);
                return Results.Json(new { success = true, yaml });
            }
            catch (LyConfigException ex)
            {
                return Results.Json(new { success = false, message = $"转换失败（语法错误）: {ex.Message}" }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"转换失败: {ex.Message}" }, statusCode: 500);
            }
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

        // =============== 负载均衡管理 API ===============

        // 获取所有集群及其目标列表
        app.MapGet("/api/lb/clusters", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var rpSection = config.GetSection("ReverseProxy:Clusters");
                var clusters = new List<object>();
                foreach (var clusterSection in rpSection.GetChildren())
                {
                    var clusterId = clusterSection.Key;
                    var policy = clusterSection["LoadBalancingPolicy"] ?? "RoundRobin";
                    var destinations = new List<object>();
                    var destSection = clusterSection.GetSection("Destinations");
                    foreach (var dest in destSection.GetChildren())
                    {
                        var address = dest["Address"] ?? "";
                        Uri.TryCreate(address, UriKind.Absolute, out var uri);
                        var metadata = new Dictionary<string, string>();
                        var metaSection = dest.GetSection("Metadata");
                        foreach (var meta in metaSection.GetChildren())
                        {
                            metadata[meta.Key] = meta.Value ?? "";
                        }
                        var destSource = DetectConfigSource(config, $"ReverseProxy:Clusters:{clusterId}:Destinations:{dest.Key}");
                        destinations.Add(new
                        {
                            id = dest.Key,
                            address,
                            host = uri?.Host ?? "*",
                            port = uri?.Port ?? 0,
                            scheme = uri?.Scheme ?? "http",
                            metadata,
                            source = destSource == ConfigSource.PatchConfig ? "patch" : "original"
                        });
                    }
                    clusters.Add(new
                    {
                        id = clusterId,
                        loadBalancingPolicy = policy,
                        destinations,
                        destinationCount = destinations.Count
                    });
                }
                return Results.Json(new { success = true, clusters, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取集群列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取所有可用负载均衡策略
        app.MapGet("/api/lb/policies", (HttpContext ctx) =>
        {
            var policies = new[]
            {
                new { name = "RoundRobin", label = "轮询" },
                new { name = "Random", label = "随机" },
                new { name = "LeastRequests", label = "最少连接" },
                new { name = "PowerOfTwoChoices", label = "二选一" },
                new { name = "First", label = "总是第一个" },
                new { name = "WeightedRoundRobin", label = "加权轮询" },
                new { name = "WeightedLeastConnections", label = "加权最少连接" },
                new { name = "IpHash", label = "IP 哈希" },
                new { name = "GenericHash", label = "通用哈希" },
                new { name = "WeightedRandom", label = "加权随机" },
                new { name = "ConsistentHash", label = "一致性哈希" },
            };
            return Results.Json(new { success = true, policies });
        }).RequireHost($"*:{controlPort}");

        // 更新集群的负载均衡策略
        app.MapPost("/api/lb/policy/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateClusterPolicyRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || string.IsNullOrEmpty(request.Policy))
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 Policy 不能为空" }, statusCode: 400);
                }

                var clusterSection = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}");
                if (!clusterSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"集群 {request.ClusterId} 不存在" }, statusCode: 400);
                }

                // 检测策略配置的来源
                var policyConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:LoadBalancingPolicy";
                var configSource = DetectConfigSource(config, policyConfigKey);

                if (configSource == ConfigSource.OriginalConfig)
                {
                    // 修改原始配置文件
                    return await ModifyOriginalConfig(config, request.ClusterId, configObj =>
                    {
                        if (configObj is not Dictionary<string, object> rootDict)
                            return "配置文件格式错误";
                            
                        if (!rootDict.TryGetValue("ReverseProxy", out var rpObj) || rpObj is not Dictionary<string, object> rpDict)
                            return "ReverseProxy 配置不存在";
                            
                        if (!rpDict.TryGetValue("Clusters", out var clustersObj) || clustersObj is not Dictionary<string, object> clustersDict)
                            return "Clusters 配置不存在";
                            
                        if (!clustersDict.TryGetValue(request.ClusterId, out var clusterObj) || clusterObj is not Dictionary<string, object> clusterDict)
                            return $"集群 {request.ClusterId} 不存在";
                            
                        clusterDict["LoadBalancingPolicy"] = request.Policy;
                        return null;
                    });
                }
                else
                {
                    // 修改补丁配置文件
                    return await ModifyLbPatch(config, patch =>
                    {
                        var cluster = EnsurePatchCluster(patch, request.ClusterId);
                        cluster["LoadBalancingPolicy"] = request.Policy;
                        return null;
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新策略失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加目标服务器
        app.MapPost("/api/lb/destinations/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddDestinationRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) ||
                    string.IsNullOrEmpty(request.DestinationId) || string.IsNullOrEmpty(request.Address))
                {
                    return Results.Json(new { success = false, message = "ClusterId、DestinationId 和 Address 不能为空" }, statusCode: 400);
                }

                // 检查集群是否存在
                var clusterSection = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}");
                if (!clusterSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"集群 {request.ClusterId} 不存在" }, statusCode: 400);
                }

                // 检查目标是否已存在
                var existDest = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}");
                if (existDest.Exists())
                {
                    return Results.Json(new { success = false, message = $"目标 {request.DestinationId} 已存在" }, statusCode: 400);
                }

                // 检测集群配置的来源（Destinations 或集群本身）
                var clusterConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}";
                var destinationsConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations";
                var configSource = DetectConfigSource(config, destinationsConfigKey);
                
                if (configSource == ConfigSource.NotFound)
                {
                    configSource = DetectConfigSource(config, clusterConfigKey);
                }

                if (configSource == ConfigSource.OriginalConfig)
                {
                    // 修改原始配置文件
                    return await ModifyOriginalConfig(config, request.ClusterId, configObj =>
                    {
                        if (configObj is not Dictionary<string, object> rootDict)
                            return "配置文件格式错误";
                            
                        if (!rootDict.TryGetValue("ReverseProxy", out var rpObj) || rpObj is not Dictionary<string, object> rpDict)
                            return "ReverseProxy 配置不存在";
                            
                        if (!rpDict.TryGetValue("Clusters", out var clustersObj) || clustersObj is not Dictionary<string, object> clustersDict)
                            return "Clusters 配置不存在";
                            
                        if (!clustersDict.TryGetValue(request.ClusterId, out var clusterObj) || clusterObj is not Dictionary<string, object> clusterDict)
                            return $"集群 {request.ClusterId} 不存在";
                            
                        // 确保Destinations存在
                        if (!clusterDict.TryGetValue("Destinations", out var destsObj) || destsObj is not Dictionary<string, object> destsDict)
                        {
                            destsDict = new Dictionary<string, object>();
                            clusterDict["Destinations"] = destsDict;
                        }
                        
                        // 添加新的目标
                        var dest = new Dictionary<string, object> { ["Address"] = request.Address };
                        if (request.Metadata != null && request.Metadata.Count > 0)
                        {
                            dest["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        destsDict[request.DestinationId] = dest;
                        return null;
                    });
                }
                else
                {
                    // 修改补丁配置文件（只写入新增的目标，不快照整个集群）
                    return await ModifyLbPatch(config, patch =>
                    {
                        var cluster = EnsurePatchCluster(patch, request.ClusterId);
                        var dests = EnsurePatchDestinations(cluster);
                        var dest = new Dictionary<string, object> { ["Address"] = request.Address };
                        if (request.Metadata != null && request.Metadata.Count > 0)
                        {
                            dest["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        dests[request.DestinationId] = dest;
                        return null;
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 编辑目标服务器
        app.MapPost("/api/lb/destinations/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateDestinationRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || string.IsNullOrEmpty(request.DestinationId))
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 DestinationId 不能为空" }, statusCode: 400);
                }

                var destSection = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}");
                if (!destSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"目标 {request.DestinationId} 不存在" }, statusCode: 400);
                }

                // 检测目标配置的来源
                var destConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}";
                var configSource = DetectConfigSource(config, destConfigKey);

                if (configSource == ConfigSource.OriginalConfig)
                {
                    // 修改原始配置文件
                    return await ModifyOriginalConfig(config, request.ClusterId, configObj =>
                    {
                        if (configObj is not Dictionary<string, object> rootDict)
                            return "配置文件格式错误";
                            
                        if (!rootDict.TryGetValue("ReverseProxy", out var rpObj) || rpObj is not Dictionary<string, object> rpDict)
                            return "ReverseProxy 配置不存在";
                            
                        if (!rpDict.TryGetValue("Clusters", out var clustersObj) || clustersObj is not Dictionary<string, object> clustersDict)
                            return "Clusters 配置不存在";
                            
                        if (!clustersDict.TryGetValue(request.ClusterId, out var clusterObj) || clusterObj is not Dictionary<string, object> clusterDict)
                            return $"集群 {request.ClusterId} 不存在";
                            
                        if (!clusterDict.TryGetValue("Destinations", out var destsObj) || destsObj is not Dictionary<string, object> destsDict)
                            return "Destinations 配置不存在";
                            
                        if (!destsDict.TryGetValue(request.DestinationId, out var destObj) || destObj is not Dictionary<string, object> destDict)
                            return $"目标 {request.DestinationId} 不存在";
                            
                        // 更新目标配置
                        if (!string.IsNullOrEmpty(request.Address)) destDict["Address"] = request.Address;
                        if (request.Metadata != null)
                        {
                            destDict["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        return null;
                    });
                }
                else
                {
                    // 修改补丁配置文件（只写入被修改的目标）
                    return await ModifyLbPatch(config, patch =>
                    {
                        var cluster = EnsurePatchCluster(patch, request.ClusterId);
                        var dests = EnsurePatchDestinations(cluster);

                        var dest = dests.TryGetValue(request.DestinationId, out var existObj) && existObj is Dictionary<string, object> existDict
                            ? existDict : new Dictionary<string, object>();

                        if (!string.IsNullOrEmpty(request.Address)) dest["Address"] = request.Address;
                        if (request.Metadata != null)
                        {
                            dest["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        dests[request.DestinationId] = dest;
                        return null;
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除目标服务器
        app.MapPost("/api/lb/destinations/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveDestinationRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || string.IsNullOrEmpty(request.DestinationId))
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 DestinationId 不能为空" }, statusCode: 400);
                }

                // 检测目标配置的来源：只允许删除补丁中的目标
                var destConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}";
                var configSource = DetectConfigSource(config, destConfigKey);

                if (configSource != ConfigSource.PatchConfig)
                {
                    return Results.Json(new { success = false, message = $"目标 {request.DestinationId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifyLbPatch(config, patch =>
                {
                    var cluster = EnsurePatchCluster(patch, request.ClusterId);
                    var dests = EnsurePatchDestinations(cluster);
                    dests.Remove(request.DestinationId);
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 批量删除目标服务器
        app.MapPost("/api/lb/destinations/batch-remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<BatchRemoveDestinationsRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || request.DestinationIds == null || request.DestinationIds.Count == 0)
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 DestinationIds 不能为空" }, statusCode: 400);
                }

                // 检测所有目标的配置来源：只允许删除补丁中的目标
                var originalIds = new List<string>();
                foreach (var destId in request.DestinationIds)
                {
                    var destConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{destId}";
                    var configSource = DetectConfigSource(config, destConfigKey);
                    if (configSource != ConfigSource.PatchConfig)
                    {
                        originalIds.Add(destId);
                    }
                }

                if (originalIds.Count > 0)
                {
                    return Results.Json(new { success = false, message = $"以下目标来自默认配置，不能删除: {string.Join(", ", originalIds)}" }, statusCode: 400);
                }

                return await ModifyLbPatch(config, patch =>
                {
                    var cluster = EnsurePatchCluster(patch, request.ClusterId);
                    var dests = EnsurePatchDestinations(cluster);
                    foreach (var destId in request.DestinationIds)
                    {
                        dests.Remove(destId);
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"批量删除目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除集群补丁（恢复为原始配置）
        app.MapPost("/api/lb/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveClusterPatchRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId))
                {
                    return Results.Json(new { success = false, message = "ClusterId 不能为空" }, statusCode: 400);
                }

                return await ModifyLbPatch(config, clusters =>
                {
                    if (!clusters.Remove(request.ClusterId))
                    {
                        return $"补丁中不存在集群 {request.ClusterId}";
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 路由管理 API ===============

        // 获取所有路由列表
        app.MapGet("/api/routes", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var rpSection = config.GetSection("ReverseProxy:Routes");
                var routes = new List<object>();
                foreach (var routeSection in rpSection.GetChildren())
                {
                    var routeId = routeSection.Key;
                    var clusterId = routeSection["ClusterId"] ?? "";
                    var order = int.TryParse(routeSection["Order"], out var o) ? o : 0;

                    // Match
                    var matchSection = routeSection.GetSection("Match");
                    var path = matchSection["Path"] ?? "";
                    var hosts = new List<string>();
                    var hostsSection = matchSection.GetSection("Hosts");
                    foreach (var h in hostsSection.GetChildren())
                    {
                        if (h.Value != null) hosts.Add(h.Value);
                    }
                    var methods = new List<string>();
                    var methodsSection = matchSection.GetSection("Methods");
                    foreach (var m in methodsSection.GetChildren())
                    {
                        if (m.Value != null) methods.Add(m.Value);
                    }

                    // 检测配置来源
                    var source = DetectConfigSource(config, $"ReverseProxy:Routes:{routeId}");
                    var sourceText = source == ConfigSource.PatchConfig ? "patch" : "original";

                    routes.Add(new
                    {
                        routeId,
                        clusterId,
                        order,
                        match = new
                        {
                            path,
                            hosts,
                            methods
                        },
                        source = sourceText
                    });
                }

                // 按 Order 排序
                routes.Sort((a, b) =>
                {
                    var orderA = ((dynamic)a).order;
                    var orderB = ((dynamic)b).order;
                    return ((int)orderA).CompareTo((int)orderB);
                });

                return Results.Json(new { success = true, routes, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取路由列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 新增路由（通过补丁）
        app.MapPost("/api/routes/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRouteRequest>();
                if (request == null || string.IsNullOrEmpty(request.RouteId))
                {
                    return Results.Json(new { success = false, message = "RouteId 不能为空" }, statusCode: 400);
                }

                // 检查路由是否已存在（在合并后的配置中）
                var existingRoute = config.GetSection($"ReverseProxy:Routes:{request.RouteId}");
                if (existingRoute.Exists())
                {
                    return Results.Json(new { success = false, message = $"路由 {request.RouteId} 已存在" }, statusCode: 400);
                }

                return await ModifyRoutePatch(config, routes =>
                {
                    // 检查补丁中是否也已存在
                    if (routes.ContainsKey(request.RouteId))
                    {
                        return $"路由 {request.RouteId} 已存在于补丁中";
                    }

                    var routePatch = new Dictionary<string, object>();

                    if (request.ClusterId != null)
                        routePatch["ClusterId"] = request.ClusterId;

                    if (request.Order.HasValue)
                        routePatch["Order"] = request.Order.Value;

                    if (request.Match != null)
                    {
                        var matchPatch = new Dictionary<string, object>();
                        if (request.Match.Path != null)
                            matchPatch["Path"] = request.Match.Path;
                        if (request.Match.Hosts != null && request.Match.Hosts.Count > 0)
                            matchPatch["Hosts"] = request.Match.Hosts.ToList<object>();
                        if (request.Match.Methods != null && request.Match.Methods.Count > 0)
                            matchPatch["Methods"] = request.Match.Methods.ToList<object>();
                        if (matchPatch.Count > 0)
                            routePatch["Match"] = matchPatch;
                    }

                    routes[request.RouteId] = routePatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"新增路由失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新路由配置（通过补丁）
        app.MapPost("/api/routes/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateRouteRequest>();
                if (request == null || string.IsNullOrEmpty(request.RouteId))
                {
                    return Results.Json(new { success = false, message = "RouteId 不能为空" }, statusCode: 400);
                }

                // 检查路由是否存在
                var routeSection = config.GetSection($"ReverseProxy:Routes:{request.RouteId}");
                if (!routeSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"路由 {request.RouteId} 不存在" }, statusCode: 400);
                }

                // 从当前配置快照需要的字段，然后用补丁覆盖
                return await ModifyRoutePatch(config, routes =>
                {
                    // 获取或创建该路由的补丁节点
                    Dictionary<string, object> routePatch;
                    if (routes.TryGetValue(request.RouteId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            routePatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            routePatch = dict;
                        else
                            routePatch = new Dictionary<string, object>();
                    }
                    else
                    {
                        routePatch = new Dictionary<string, object>();
                    }

                    // 快照当前配置中的基础字段到补丁（如果补丁中还没有）
                    if (!routePatch.ContainsKey("ClusterId"))
                    {
                        var clusterId = routeSection["ClusterId"];
                        if (clusterId != null) routePatch["ClusterId"] = clusterId;
                    }
                    if (!routePatch.ContainsKey("Order"))
                    {
                        var order = routeSection["Order"];
                        if (order != null) routePatch["Order"] = int.TryParse(order, out var o) ? o : (object)order;
                    }
                    if (!routePatch.ContainsKey("Match"))
                    {
                        var matchSection = routeSection.GetSection("Match");
                        var match = new Dictionary<string, object>();
                        var path = matchSection["Path"];
                        if (path != null) match["Path"] = path;

                        var hostsSection = matchSection.GetSection("Hosts");
                        var hostsList = hostsSection.GetChildren().Select(h => h.Value).Where(v => v != null).ToList();
                        if (hostsList.Count > 0) match["Hosts"] = hostsList;

                        var methodsSection = matchSection.GetSection("Methods");
                        var methodsList = methodsSection.GetChildren().Select(m => m.Value).Where(v => v != null).ToList();
                        if (methodsList.Count > 0) match["Methods"] = methodsList;

                        if (match.Count > 0) routePatch["Match"] = match;
                    }

                    // 应用请求中的更新
                    if (request.Order.HasValue)
                    {
                        routePatch["Order"] = request.Order.Value;
                    }
                    if (request.ClusterId != null)
                    {
                        routePatch["ClusterId"] = request.ClusterId;
                    }
                    if (request.Match != null)
                    {
                        Dictionary<string, object> matchPatch;
                        if (routePatch.TryGetValue("Match", out var matchObj) && matchObj is Dictionary<string, object> existMatch)
                        {
                            matchPatch = existMatch;
                        }
                        else
                        {
                            matchPatch = new Dictionary<string, object>();
                        }

                        if (request.Match.Path != null)
                        {
                            matchPatch["Path"] = request.Match.Path;
                        }
                        if (request.Match.Hosts != null)
                        {
                            if (request.Match.Hosts.Count > 0)
                                matchPatch["Hosts"] = request.Match.Hosts.ToList<object>();
                            else
                                matchPatch.Remove("Hosts");
                        }
                        if (request.Match.Methods != null)
                        {
                            if (request.Match.Methods.Count > 0)
                                matchPatch["Methods"] = request.Match.Methods.ToList<object>();
                            else
                                matchPatch.Remove("Methods");
                        }
                        routePatch["Match"] = matchPatch;
                    }

                    routes[request.RouteId] = routePatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新路由失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除路由（仅支持删除补丁中新增的路由）
        app.MapPost("/api/routes/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRoutePatchRequest>();
                if (request == null || string.IsNullOrEmpty(request.RouteId))
                {
                    return Results.Json(new { success = false, message = "RouteId 不能为空" }, statusCode: 400);
                }

                // 检测来源：如果原始配置中存在，不允许删除（只能删除补丁新增的）
                var source = DetectConfigSource(config, $"ReverseProxy:Routes:{request.RouteId}");
                if (source == ConfigSource.OriginalConfig)
                {
                    return Results.Json(new { success = false, message = $"路由 {request.RouteId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifyRoutePatch(config, routes =>
                {
                    if (!routes.Remove(request.RouteId))
                    {
                        return $"补丁中不存在路由 {request.RouteId}";
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除路由失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除路由补丁（恢复为原始配置）
        app.MapPost("/api/routes/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRoutePatchRequest>();
                if (request == null)
                {
                    return Results.Json(new { success = false, message = "请求格式错误" }, statusCode: 400);
                }

                return await ModifyRoutePatch(config, routes =>
                {
                    if (string.IsNullOrEmpty(request.RouteId))
                    {
                        // 清空所有路由补丁
                        routes.Clear();
                    }
                    else
                    {
                        // 删除指定路由补丁
                        if (!routes.Remove(request.RouteId))
                        {
                            return $"补丁中不存在路由 {request.RouteId}";
                        }
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除路由补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 文件服务配置 API ===============

        // 获取所有文件服务配置
        app.MapGet("/api/fileserver", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var fsSection = config.GetSection("FileServer:Items");
                var items = new List<object>();
                foreach (var itemSection in fsSection.GetChildren())
                {
                    var itemId = itemSection.Key;
                    var prefix = itemSection["Prefix"] ?? "/";
                    var basePath = itemSection["BasePath"] ?? "wwwroot";
                    var browse = bool.TryParse(itemSection["Browse"], out var b) && b;
                    var preCompressed = bool.TryParse(itemSection["PreCompressed"], out var pc) && pc;
                    var maxFileSize = long.TryParse(itemSection["MaxFileSize"], out var mfs) ? mfs : 100L * 1024 * 1024;

                    var tryFiles = new List<string>();
                    var tryFilesSection = itemSection.GetSection("TryFiles");
                    foreach (var tf in tryFilesSection.GetChildren())
                    {
                        if (tf.Value != null) tryFiles.Add(tf.Value);
                    }

                    var defaultFiles = new List<string>();
                    var defaultSection = itemSection.GetSection("Default");
                    foreach (var df in defaultSection.GetChildren())
                    {
                        if (df.Value != null) defaultFiles.Add(df.Value);
                    }

                    var source = DetectConfigSource(config, $"FileServer:Items:{itemId}");
                    items.Add(new
                    {
                        itemId,
                        prefix,
                        basePath,
                        browse,
                        preCompressed,
                        maxFileSize,
                        tryFiles,
                        defaultFiles,
                        source = source == ConfigSource.PatchConfig ? "patch" : "original"
                    });
                }
                return Results.Json(new { success = true, items, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取文件服务配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 新增文件服务配置（通过补丁）
        app.MapPost("/api/fileserver/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddFileServerRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var existing = config.GetSection($"FileServer:Items:{request.ItemId}");
                if (existing.Exists())
                {
                    return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 已存在" }, statusCode: 400);
                }

                return await ModifyFileServerPatch(config, fileservers =>
                {
                    if (fileservers.ContainsKey(request.ItemId))
                        return $"文件服务 {request.ItemId} 已存在于补丁中";

                    var item = new Dictionary<string, object>();
                    if (request.Prefix != null) item["Prefix"] = request.Prefix;
                    if (request.BasePath != null) item["BasePath"] = request.BasePath;
                    if (request.Browse.HasValue) item["Browse"] = request.Browse.Value;
                    if (request.PreCompressed.HasValue) item["PreCompressed"] = request.PreCompressed.Value;
                    if (request.MaxFileSize.HasValue) item["MaxFileSize"] = request.MaxFileSize.Value;
                    if (request.TryFiles != null && request.TryFiles.Count > 0)
                        item["TryFiles"] = request.TryFiles.ToList<object>();
                    if (request.DefaultFiles != null && request.DefaultFiles.Count > 0)
                        item["Default"] = request.DefaultFiles.ToList<object>();

                    fileservers[request.ItemId] = item;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"新增文件服务失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新文件服务配置（通过补丁）
        app.MapPost("/api/fileserver/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateFileServerRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var itemSection = config.GetSection($"FileServer:Items:{request.ItemId}");
                if (!itemSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 不存在" }, statusCode: 400);
                }

                return await ModifyFileServerPatch(config, fileservers =>
                {
                    Dictionary<string, object> itemPatch;
                    if (fileservers.TryGetValue(request.ItemId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            itemPatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            itemPatch = dict;
                        else
                            itemPatch = new Dictionary<string, object>();
                    }
                    else
                    {
                        // 快照当前配置到补丁
                        itemPatch = new Dictionary<string, object>();
                        var section = config.GetSection($"FileServer:Items:{request.ItemId}");
                        var prefix = section["Prefix"]; if (prefix != null) itemPatch["Prefix"] = prefix;
                        var basePath = section["BasePath"]; if (basePath != null) itemPatch["BasePath"] = basePath;
                        var browse = section["Browse"]; if (browse != null) itemPatch["Browse"] = bool.TryParse(browse, out var bv) && bv;
                        var preCompressed = section["PreCompressed"]; if (preCompressed != null) itemPatch["PreCompressed"] = bool.TryParse(preCompressed, out var pcv) && pcv;
                        var maxFileSize = section["MaxFileSize"]; if (maxFileSize != null && long.TryParse(maxFileSize, out var mfv)) itemPatch["MaxFileSize"] = mfv;

                        var tryFilesSection = section.GetSection("TryFiles");
                        var tfList = tryFilesSection.GetChildren().Select(c => c.Value).Where(v => v != null).Cast<object>().ToList();
                        if (tfList.Count > 0) itemPatch["TryFiles"] = tfList;

                        var defaultSection = section.GetSection("Default");
                        var dfList = defaultSection.GetChildren().Select(c => c.Value).Where(v => v != null).Cast<object>().ToList();
                        if (dfList.Count > 0) itemPatch["Default"] = dfList;
                    }

                    // 应用更新
                    if (request.Prefix != null) itemPatch["Prefix"] = request.Prefix;
                    if (request.BasePath != null) itemPatch["BasePath"] = request.BasePath;
                    if (request.Browse.HasValue) itemPatch["Browse"] = request.Browse.Value;
                    if (request.PreCompressed.HasValue) itemPatch["PreCompressed"] = request.PreCompressed.Value;
                    if (request.MaxFileSize.HasValue) itemPatch["MaxFileSize"] = request.MaxFileSize.Value;
                    if (request.TryFiles != null)
                    {
                        if (request.TryFiles.Count > 0)
                            itemPatch["TryFiles"] = request.TryFiles.ToList<object>();
                        else
                            itemPatch.Remove("TryFiles");
                    }
                    if (request.DefaultFiles != null)
                    {
                        if (request.DefaultFiles.Count > 0)
                            itemPatch["Default"] = request.DefaultFiles.ToList<object>();
                        else
                            itemPatch.Remove("Default");
                    }

                    fileservers[request.ItemId] = itemPatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新文件服务失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除文件服务（仅补丁中新增的）
        app.MapPost("/api/fileserver/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveFileServerRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var source = DetectConfigSource(config, $"FileServer:Items:{request.ItemId}");
                if (source != ConfigSource.PatchConfig)
                {
                    return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifyFileServerPatch(config, fileservers =>
                {
                    if (!fileservers.Remove(request.ItemId))
                        return $"补丁中不存在文件服务 {request.ItemId}";
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除文件服务失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除所有文件服务补丁
        app.MapPost("/api/fileserver/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                return await ModifyFileServerPatch(config, fileservers =>
                {
                    fileservers.Clear();
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除文件服务补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 简单响应配置 API ===============

        // 获取所有简单响应配置
        app.MapGet("/api/simpleres", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var srSection = config.GetSection("SimpleRes:Items");
                var items = new List<object>();
                foreach (var itemSection in srSection.GetChildren())
                {
                    var itemId = itemSection.Key;
                    var body = itemSection["Body"] ?? "";
                    var contentType = itemSection["ContentType"] ?? "text/plain";
                    var statusCode = int.TryParse(itemSection["StatusCode"], out var sc) ? sc : 200;
                    var charset = itemSection["Charset"] ?? "utf-8";
                    var showReq = bool.TryParse(itemSection["ShowReq"], out var sr) && sr;

                    var headers = new Dictionary<string, string>();
                    var headersSection = itemSection.GetSection("Headers");
                    foreach (var h in headersSection.GetChildren())
                    {
                        if (h.Value != null) headers[h.Key] = h.Value;
                    }

                    var source = DetectConfigSource(config, $"SimpleRes:Items:{itemId}");
                    items.Add(new
                    {
                        itemId,
                        body,
                        contentType,
                        statusCode,
                        charset,
                        showReq,
                        headers,
                        source = source == ConfigSource.PatchConfig ? "patch" : "original"
                    });
                }
                return Results.Json(new { success = true, items, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取简单响应配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 新增简单响应配置（通过补丁）
        app.MapPost("/api/simpleres/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddSimpleResRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var existing = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                if (existing.Exists())
                {
                    return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 已存在" }, statusCode: 400);
                }

                return await ModifySimpleResPatch(config, simpleres =>
                {
                    if (simpleres.ContainsKey(request.ItemId))
                        return $"简单响应 {request.ItemId} 已存在于补丁中";

                    var item = new Dictionary<string, object>();
                    if (request.Body != null) item["Body"] = request.Body;
                    if (request.ContentType != null) item["ContentType"] = request.ContentType;
                    if (request.StatusCode.HasValue) item["StatusCode"] = request.StatusCode.Value;
                    if (request.Charset != null) item["Charset"] = request.Charset;
                    if (request.ShowReq.HasValue) item["ShowReq"] = request.ShowReq.Value;
                    if (request.Headers != null && request.Headers.Count > 0)
                        item["Headers"] = request.Headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

                    simpleres[request.ItemId] = item;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"新增简单响应失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新简单响应配置（通过补丁）
        app.MapPost("/api/simpleres/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateSimpleResRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var itemSection = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                if (!itemSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 不存在" }, statusCode: 400);
                }

                return await ModifySimpleResPatch(config, simpleres =>
                {
                    Dictionary<string, object> itemPatch;
                    if (simpleres.TryGetValue(request.ItemId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            itemPatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            itemPatch = dict;
                        else
                            itemPatch = new Dictionary<string, object>();
                    }
                    else
                    {
                        // 快照当前配置到补丁
                        itemPatch = new Dictionary<string, object>();
                        var section = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                        var body = section["Body"]; if (body != null) itemPatch["Body"] = body;
                        var ct = section["ContentType"]; if (ct != null) itemPatch["ContentType"] = ct;
                        var sc = section["StatusCode"]; if (sc != null && int.TryParse(sc, out var scv)) itemPatch["StatusCode"] = scv;
                        var cs = section["Charset"]; if (cs != null) itemPatch["Charset"] = cs;
                        var srq = section["ShowReq"]; if (srq != null) itemPatch["ShowReq"] = bool.TryParse(srq, out var srv) && srv;

                        var headersSection = section.GetSection("Headers");
                        var hDict = new Dictionary<string, object>();
                        foreach (var h in headersSection.GetChildren())
                        {
                            if (h.Value != null) hDict[h.Key] = h.Value;
                        }
                        if (hDict.Count > 0) itemPatch["Headers"] = hDict;
                    }

                    // 应用更新
                    if (request.Body != null) itemPatch["Body"] = request.Body;
                    if (request.ContentType != null) itemPatch["ContentType"] = request.ContentType;
                    if (request.StatusCode.HasValue) itemPatch["StatusCode"] = request.StatusCode.Value;
                    if (request.Charset != null) itemPatch["Charset"] = request.Charset;
                    if (request.ShowReq.HasValue) itemPatch["ShowReq"] = request.ShowReq.Value;
                    if (request.Headers != null)
                    {
                        if (request.Headers.Count > 0)
                            itemPatch["Headers"] = request.Headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        else
                            itemPatch.Remove("Headers");
                    }

                    simpleres[request.ItemId] = itemPatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新简单响应失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除简单响应（仅补丁中新增的）
        app.MapPost("/api/simpleres/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveSimpleResRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var source = DetectConfigSource(config, $"SimpleRes:Items:{request.ItemId}");
                if (source != ConfigSource.PatchConfig)
                {
                    return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifySimpleResPatch(config, simpleres =>
                {
                    if (!simpleres.Remove(request.ItemId))
                        return $"补丁中不存在简单响应 {request.ItemId}";
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除简单响应失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除所有简单响应补丁
        app.MapPost("/api/simpleres/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                return await ModifySimpleResPatch(config, simpleres =>
                {
                    simpleres.Clear();
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除简单响应补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        return app;
    }

    // =============== 配置来源检测辅助方法 ===============

    /// <summary>
    /// 配置来源枚举
    /// </summary>
    private enum ConfigSource
    {
        OriginalConfig,  // 原始配置文件 (.ly 或 .yaml)
        PatchConfig,     // 补丁配置文件 (.lywaf.lb-patch.json)
        NotFound         // 配置不存在
    }

    /// <summary>
    /// 检测指定配置项的来源
    /// </summary>
    /// <param name="config">配置对象</param>
    /// <param name="key">配置键</param>
    /// <returns>配置来源</returns>
    private static ConfigSource DetectConfigSource(IConfiguration config, string key)
    {
        // 检查是否存在于补丁配置中
        var patchPath = LbPatchConfig.GetPatchFilePath();
        if (File.Exists(patchPath))
        {
            try
            {
                var json = File.ReadAllText(patchPath, Encoding.UTF8);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                
                // 将配置键转换为补丁文件中的路径
                var patchKey = key;
                if (patchKey.StartsWith("ReverseProxy:"))
                {
                    patchKey = patchKey.Substring("ReverseProxy:".Length);
                }
                
                if (FindValueInJsonElement(doc.RootElement, patchKey))
                {
                    return ConfigSource.PatchConfig;
                }
            }
            catch (Exception)
            {
                // 补丁文件读取失败，继续检查原始配置
            }
        }
        
        // 检查是否存在于原始配置中
        var configPath = SharedData.ConfigFilePath;
        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath, Encoding.UTF8);
                
                if (configPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    configPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    // YAML 文件解析
                    using var reader = new StringReader(content);
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .Build();
                    var yamlObj = deserializer.Deserialize(reader);
                    if (yamlObj != null && FindValueInYamlObject(yamlObj, key))
                    {
                        return ConfigSource.OriginalConfig;
                    }
                }
                else if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    // JSON 文件解析
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    if (FindValueInJsonElement(doc.RootElement, key))
                    {
                        return ConfigSource.OriginalConfig;
                    }
                }
            }
            catch (Exception)
            {
                // 原始配置文件读取失败
            }
        }
        
        return ConfigSource.NotFound;
    }

    /// <summary>
    /// 在 JsonElement 中查找指定的键路径
    /// </summary>
    private static bool FindValueInJsonElement(System.Text.Json.JsonElement element, string keyPath)
    {
        var parts = keyPath.Split(':');
        var current = element;
        
        foreach (var part in parts)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
                
            if (!current.TryGetProperty(part, out current))
                return false;
        }
        
        return true;
    }

    /// <summary>
    /// 在 YAML 对象中查找指定的键路径
    /// </summary>
    private static bool FindValueInYamlObject(object yamlObj, string keyPath)
    {
        var parts = keyPath.Split(':');
        var current = yamlObj;
        
        foreach (var part in parts)
        {
            if (current is not Dictionary<object, object> dict)
                return false;
                
            if (!dict.TryGetValue(part, out current))
                return false;
        }
        
        return current != null;
    }

    // =============== 补丁文件辅助方法 ===============

    private static readonly SemaphoreSlim _lbPatchLock = new(1, 1);

    /// <summary>
    /// 读取补丁文件并反序列化为可操作的字典
    /// </summary>
    private static async Task<Dictionary<string, object>> ReadPatchFile()
    {
        var patchPath = LbPatchConfig.GetPatchFilePath();
        Dictionary<string, object> patch;
        if (File.Exists(patchPath))
        {
            var json = await File.ReadAllTextAsync(patchPath, Encoding.UTF8);
            patch = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }) ?? new();
        }
        else
        {
            patch = new Dictionary<string, object>();
        }
        return patch;
    }

    /// <summary>
    /// 确保补丁中指定节点存在，并返回可操作的字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchSection(Dictionary<string, object> patch, string sectionName)
    {
        if (!patch.ContainsKey(sectionName))
            patch[sectionName] = new Dictionary<string, object>();

        var sectionObj = patch[sectionName];
        Dictionary<string, object> section;
        if (sectionObj is System.Text.Json.JsonElement je)
        {
            section = JsonElementToDict(je);
        }
        else if (sectionObj is Dictionary<string, object> dict)
        {
            section = dict;
        }
        else
        {
            section = new Dictionary<string, object>();
        }
        patch[sectionName] = section;
        return section;
    }

    /// <summary>
    /// 更新补丁中的源文件跟踪信息
    /// </summary>
    private static void UpdatePatchSourceTracking(Dictionary<string, object> patch)
    {
        var configPath = SharedData.ConfigFilePath;
        if (File.Exists(configPath))
        {
            var lastModified = File.GetLastWriteTimeUtc(configPath);
            patch["_source"] = new Dictionary<string, object>
            {
                ["file"] = Path.GetFileName(configPath),
                ["lastModified"] = lastModified.ToString("o")
            };
        }
    }

    /// <summary>
    /// 保存补丁文件并触发配置重载
    /// </summary>
    private static async Task SavePatchAndReload(Dictionary<string, object> patch, IConfiguration config)
    {
        var patchPath = LbPatchConfig.GetPatchFilePath();
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var newJson = System.Text.Json.JsonSerializer.Serialize(patch, options);
        await File.WriteAllTextAsync(patchPath, newJson, Encoding.UTF8);

        if (config is IConfigurationRoot configRoot)
        {
            configRoot.Reload();
        }
    }

    /// <summary>
    /// 修改集群补丁并触发配置重载
    /// modifier 接收 Clusters 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyLbPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var clusters = EnsurePatchSection(patch, "Clusters");

            var error = modifier(clusters);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改路由补丁并触发配置重载
    /// modifier 接收 Routes 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyRoutePatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var routes = EnsurePatchSection(patch, "Routes");

            var error = modifier(routes);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 FileServer 节点
    /// modifier 接收 FileServer 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyFileServerPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var fileservers = EnsurePatchSection(patch, "FileServer");

            var error = modifier(fileservers);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 SimpleRes 节点
    /// </summary>
    private static async Task<IResult> ModifySimpleResPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var simpleres = EnsurePatchSection(patch, "SimpleRes");

            var error = modifier(simpleres);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改原始配置文件并触发配置重载
    /// modifier 返回 null 表示成功，返回字符串表示错误信息
    /// </summary>
    private static async Task<IResult> ModifyOriginalConfig(IConfiguration config, string clusterId, Func<object, string?> modifier)
    {
        var configPath = SharedData.ConfigFilePath;
        if (!File.Exists(configPath))
        {
            return Results.Json(new { success = false, message = "配置文件不存在" }, statusCode: 500);
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
            object configObj;
            
            if (configPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                configPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                // YAML 文件处理
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                var serializer = new SerializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
                    .Build();
                    
                using var reader = new StringReader(content);
                configObj = deserializer.Deserialize(reader) ?? new Dictionary<string, object>();
                
                // 执行修改
                var error = modifier(configObj);
                if (error != null)
                {
                    return Results.Json(new { success = false, message = error }, statusCode: 400);
                }
                
                // 保存 YAML 文件
                var newYaml = serializer.Serialize(configObj);
                await File.WriteAllTextAsync(configPath, newYaml, Encoding.UTF8);
            }
            else if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // JSON 文件处理
                var jsonOptions = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = false 
                };
                
                configObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content) 
                           ?? new Dictionary<string, object>();
                
                // 执行修改
                var error = modifier(configObj);
                if (error != null)
                {
                    return Results.Json(new { success = false, message = error }, statusCode: 400);
                }
                
                // 保存 JSON 文件
                var newJson = System.Text.Json.JsonSerializer.Serialize(configObj, jsonOptions);
                await File.WriteAllTextAsync(configPath, newJson, Encoding.UTF8);
            }
            else
            {
                return Results.Json(new { success = false, message = "不支持的配置文件格式" }, statusCode: 500);
            }

            // 触发配置重载
            if (config is IConfigurationRoot configRoot)
            {
                configRoot.Reload();
            }

            return Results.Json(new { success = true, message = "原始配置已更新并重载" });
        }
        catch (Exception ex)
        {
            return Results.Json(new { success = false, message = $"修改原始配置失败: {ex.Message}" }, statusCode: 500);
        }
    }

    /// <summary>
    /// 确保补丁中存在指定集群节点，返回该集群字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchCluster(Dictionary<string, object> clusters, string clusterId)
    {
        if (clusters.TryGetValue(clusterId, out var clusterObj))
        {
            if (clusterObj is System.Text.Json.JsonElement je)
            {
                var dict = JsonElementToDict(je);
                clusters[clusterId] = dict;
                return dict;
            }
            if (clusterObj is Dictionary<string, object> existing)
                return existing;
        }
        var newCluster = new Dictionary<string, object>();
        clusters[clusterId] = newCluster;
        return newCluster;
    }

    /// <summary>
    /// 确保补丁集群字典中存在 Destinations 子字典，不存在则创建空字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchDestinations(Dictionary<string, object> clusterPatch)
    {
        if (clusterPatch.TryGetValue("Destinations", out var destsObj))
        {
            if (destsObj is System.Text.Json.JsonElement je)
            {
                var dict = JsonElementToDict(je);
                clusterPatch["Destinations"] = dict;
                return dict;
            }
            if (destsObj is Dictionary<string, object> existing)
                return existing;
        }
        var newDests = new Dictionary<string, object>();
        clusterPatch["Destinations"] = newDests;
        return newDests;
    }

    /// <summary>
    /// [已废弃] 从 IConfiguration 快照当前集群的 Destinations 和 LoadBalancingPolicy 到补丁字典
    /// </summary>
    private static void SnapshotClusterDestinations(IConfiguration config, string clusterId, Dictionary<string, object> clusterPatch)
    {
        var clusterSection = config.GetSection($"ReverseProxy:Clusters:{clusterId}");

        // 保持策略
        if (!clusterPatch.ContainsKey("LoadBalancingPolicy"))
        {
            var policy = clusterSection["LoadBalancingPolicy"];
            if (policy != null) clusterPatch["LoadBalancingPolicy"] = policy;
        }

        // 快照 destinations（如果补丁中还没有）
        if (!clusterPatch.ContainsKey("Destinations"))
        {
            var dests = new Dictionary<string, object>();
            var destsSection = clusterSection.GetSection("Destinations");
            foreach (var dest in destsSection.GetChildren())
            {
                var destDict = new Dictionary<string, object>();
                var address = dest["Address"];
                if (address != null) destDict["Address"] = address;

                var metaSection = dest.GetSection("Metadata");
                var metaChildren = metaSection.GetChildren().ToList();
                if (metaChildren.Count > 0)
                {
                    var meta = new Dictionary<string, object>();
                    foreach (var m in metaChildren)
                    {
                        if (m.Value != null) meta[m.Key] = m.Value;
                    }
                    destDict["Metadata"] = meta;
                }

                dests[dest.Key] = destDict;
            }
            clusterPatch["Destinations"] = dests;
        }
    }

    /// <summary>
    /// 递归将 JsonElement 转换为 Dictionary
    /// </summary>
    private static Dictionary<string, object> JsonElementToDict(System.Text.Json.JsonElement element)
    {
        var dict = new Dictionary<string, object>();
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return dict;

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Object => JsonElementToDict(prop.Value),
                System.Text.Json.JsonValueKind.String => prop.Value.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => prop.Value.ToString(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                _ => prop.Value.ToString()
            };
        }
        return dict;
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

public class SaveConfigRequest
{
    public string Content { get; set; } = "";
    public bool Reload { get; set; }
}

public class ConvertConfigRequest
{
    public string Content { get; set; } = "";
}

// =============== 负载均衡请求模型 ===============

public class UpdateClusterPolicyRequest
{
    public string ClusterId { get; set; } = "";
    public string Policy { get; set; } = "";
}

public class AddDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string Address { get; set; } = "";
    public Dictionary<string, string>? Metadata { get; set; }
}

public class UpdateDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string? Address { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class RemoveDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
}

public class BatchRemoveDestinationsRequest
{
    public string ClusterId { get; set; } = "";
    public List<string> DestinationIds { get; set; } = [];
}

public class RemoveClusterPatchRequest
{
    public string ClusterId { get; set; } = "";
}

public class UpdateRouteRequest
{
    public string RouteId { get; set; } = "";
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class UpdateRouteMatchRequest
{
    public string? Path { get; set; }
    public List<string>? Hosts { get; set; }
    public List<string>? Methods { get; set; }
}

public class AddRouteRequest
{
    public string RouteId { get; set; } = "";
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class RemoveRoutePatchRequest
{
    public string RouteId { get; set; } = "";
}

public class AddFileServerRequest
{
    public string ItemId { get; set; } = "";
    public string? Prefix { get; set; }
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? TryFiles { get; set; }
    public List<string>? DefaultFiles { get; set; }
}

public class UpdateFileServerRequest
{
    public string ItemId { get; set; } = "";
    public string? Prefix { get; set; }
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? TryFiles { get; set; }
    public List<string>? DefaultFiles { get; set; }
}

public class RemoveFileServerRequest
{
    public string ItemId { get; set; } = "";
}

public class AddSimpleResRequest
{
    public string ItemId { get; set; } = "";
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public int? StatusCode { get; set; }
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class UpdateSimpleResRequest
{
    public string ItemId { get; set; } = "";
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public int? StatusCode { get; set; }
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class RemoveSimpleResRequest
{
    public string ItemId { get; set; } = "";
}
