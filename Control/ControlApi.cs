using System.Diagnostics;
using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Shared;
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
    
    /// <summary>
    /// 注册控制台 API 路由
    /// </summary>
    public static WebApplication MapControlApi(this WebApplication app, WafInfoOptions wafInfos)
    {
        var controlListen = wafInfos.GetControlListen();
        var controlPort = controlListen.Port;
        
        // 概览页面（HTML）
        app.MapGet("/", (HttpContext ctx, 
            IAccessControlService accessControlService,
            IStatisticService statisticService,
            IProtectService protectService,
            IABTestService abTestService) =>
        {
            var process = Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            var connectionStats = accessControlService.GetConnectionStats();
            var blockedIps = SharedData.ClientFb;
            var abTests = abTestService.GetAllConfigs();
            
            var html = ControlTemplate.GenerateDashboardHtml(
                process, uptime, connectionStats, blockedIps, 
                accessControlService, statisticService, protectService, abTests);
            
            return Results.Content(html, "text/html; charset=utf-8");
        }).RequireHost($"*:{controlPort}");

        // 静态 JS 文件服务
        app.MapGet("/js/{filename}", (HttpContext ctx, string filename) =>
        {
            // 安全检查：只允许 .js 文件
            if (!filename.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
            {
                return Results.NotFound();
            }
            
            var jsPath = Path.Combine(AppContext.BaseDirectory, "control_html", "js", filename);
            
            // 如果在 BaseDirectory 下找不到，尝试在当前目录下找
            if (!File.Exists(jsPath))
            {
                jsPath = Path.Combine(Directory.GetCurrentDirectory(), "control_html", "js", filename);
            }
            
            if (!File.Exists(jsPath))
            {
                return Results.NotFound();
            }
            
            var content = File.ReadAllText(jsPath);
            return Results.Content(content, "application/javascript; charset=utf-8");
        }).RequireHost($"*:{controlPort}");

        // API 耗时统计页面
        app.MapGet("/api-timing", (HttpContext ctx) =>
        {
            var html = ControlTemplate.GetApiTimingTemplate();
            return Results.Content(html, "text/html; charset=utf-8");
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
                        lastRequestTime = item.LastRequestTime
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
                
                return Results.Json(new
                {
                    success = true,
                    data,
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
            var blockedIps = SharedData.ClientFb.GetSnapshot()
                .Select(kv => new
                {
                    ip = kv.Key,
                    reason = kv.Value,
                    expiresAt = SharedData.ClientFb.GetExpiration(kv.Key)
                })
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

                if (SharedData.ClientFb.Remove(request.Ip.Trim()))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已解封 IP: {request.Ip}",
                        ip = request.Ip,
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
            var count = SharedData.ClientFb.Count;
            SharedData.ClientFb.Clear();
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
