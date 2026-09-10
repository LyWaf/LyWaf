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
    private sealed record ConfigurationFileResponse(bool Success, string FileName, string Format, string Content,
        string? DraftContent, bool HasDraft, bool DraftLoaded);
    private sealed record ConfigurationSavedResponse(bool Success, string Message, string DraftFile, string VersionId);
    private static void MapConfigFileEndpoints(WebApplication app, int controlPort, WafInfoOptions wafInfos)
    {
        // =============== 配置文件管理 API ===============

        // 获取配置文件内容（优先返回草稿内容，同时返回原始文件内容）
        app.MapGet("/api/config/file", (HttpContext ctx) =>
        {
                var filePath = SharedData.ConfigFilePath;
                if (!File.Exists(filePath))
                {
                    return Results.Json(new { success = false, message = $"配置文件不存在: {filePath}" }, statusCode: 404);
                }

                var originalContent = File.ReadAllText(filePath, Encoding.UTF8);
                var fileName = Path.GetFileName(filePath);
                var format = filePath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase) ? "ly" : "yaml";

                // 检查是否存在草稿文件
                var dir = Path.GetDirectoryName(filePath) ?? ".";
                var ext = Path.GetExtension(filePath);
                var draftPath = Path.Combine(dir, $".lywaf.draft{ext}");
                var draftExists = File.Exists(draftPath);
                string? draftContent = null;
                if (draftExists)
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
                    draftLoaded = SharedData.ExistsDraftConfig
                });
        }).Produces<ConfigurationFileResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 保存配置文件内容（保存到草稿文件，不覆盖原始配置）
        app.MapPost("/api/config/file", async (HttpContext ctx, IConfiguration config, SaveConfigRequest request) =>
        {
            try
            {

                if (request == null || string.IsNullOrEmpty(request.Content))
                {
                    return Results.Json(new { success = false, message = "请求内容为空" }, statusCode: 400);
                }

                if (request.Reload) return QueueConfigurationChange(ctx, config, request.Content, null);

                var versionService = ctx.RequestServices.GetRequiredService<ConfigVersionService>();
                var version = await versionService.WriteDraftAsync(request.Content,
                    "save", ctx.RequestAborted);
                var draftPath = version.TargetPath;

                var message = $"草稿已保存到 {Path.GetFileName(draftPath)}";

                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, "保存配置草稿", clientIp);

                return Results.Json(new
                {
                    success = true,
                    message,
                    draftFile = Path.GetFileName(draftPath),
                    versionId = version.Id
                });
            }
            catch (LyConfigException ex)
            {
                return Results.Json(new { success = false, message = $"语法错误: {ex.Message}" }, statusCode: 400);
            }
            catch (InvalidDataException ex)
            {
                return Results.Json(new { success = false, message = $"配置校验失败: {ex.Message}" }, statusCode: 400);
            }
        }).Produces<LyWaf.Services.ManagementTasks.ManagementJobResponse>(202)
            .Produces<ConfigurationSavedResponse>()
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 将 .ly 内容转换为 YAML 预览
        app.MapPost("/api/config/convert", async (HttpContext ctx, ConvertConfigRequest request) =>
        {
            try
            {

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

                // 分步转换：先得到 appsettings 字典，合并补丁后再输出 YAML
                var config = LyConfigParser.Parse(request.Content, variables);
                var appSettings = LyToAppSettingsConverter.TransformToAppSettings(config);

                // 读取补丁文件并合并到 appSettings
                try
                {
                    var patchPath = LbPatchConfig.GetPatchFilePath();
                    if (File.Exists(patchPath))
                    {
                        var patchJson = await File.ReadAllTextAsync(patchPath, Encoding.UTF8);
                        using var patchDoc = System.Text.Json.JsonDocument.Parse(patchJson);
                        var root = patchDoc.RootElement;

                        // 合并 Routes 补丁 → ReverseProxy.Routes
                        if (root.TryGetProperty("Routes", out var routesEl))
                        {
                            var rp = EnsureNestedDict(appSettings, "ReverseProxy");
                            var routes = EnsureNestedDict(rp, "Routes");
                            foreach (var route in routesEl.EnumerateObject())
                            {
                                routes[route.Name] = JsonElementToYamlDict(route.Value);
                            }
                        }

                        // 合并 Clusters 补丁 → ReverseProxy.Clusters
                        if (root.TryGetProperty("Clusters", out var clustersEl))
                        {
                            var rp = EnsureNestedDict(appSettings, "ReverseProxy");
                            var clusters = EnsureNestedDict(rp, "Clusters");
                            foreach (var cluster in clustersEl.EnumerateObject())
                            {
                                clusters[cluster.Name] = JsonElementToYamlDict(cluster.Value);
                            }
                        }

                        // 合并 FileServer 补丁 → FileServer.Items
                        if (root.TryGetProperty("FileServer", out var fsEl))
                        {
                            var fs = EnsureNestedDict(appSettings, "FileServer");
                            var items = EnsureNestedDict(fs, "Items");
                            foreach (var item in fsEl.EnumerateObject())
                            {
                                items[item.Name] = JsonElementToYamlDict(item.Value);
                            }
                        }

                        // 合并 SimpleRes 补丁 → SimpleRes.Items
                        if (root.TryGetProperty("SimpleRes", out var srEl))
                        {
                            var sr = EnsureNestedDict(appSettings, "SimpleRes");
                            var items = EnsureNestedDict(sr, "Items");
                            foreach (var item in srEl.EnumerateObject())
                            {
                                items[item.Name] = JsonElementToYamlDict(item.Value);
                            }
                        }

                        // 合并 Listens 补丁 → WafInfos.Listens
                        if (root.TryGetProperty("Listens", out var listensEl))
                        {
                            var waf = EnsureNestedDict(appSettings, "WafInfos");
                            // Listens 是一个列表
                            List<object> listensList;
                            if (waf.TryGetValue("Listens", out var existingListens) && existingListens is List<object> el)
                            {
                                listensList = el;
                            }
                            else
                            {
                                listensList = new List<object>();
                                waf["Listens"] = listensList;
                            }

                            foreach (var listen in listensEl.EnumerateObject())
                            {
                                if (int.TryParse(listen.Name, out var idx))
                                {
                                    var listenDict = JsonElementToYamlDict(listen.Value);
                                    if (idx < listensList.Count)
                                    {
                                        // 覆盖已有项
                                        if (listensList[idx] is Dictionary<string, object> existing)
                                        {
                                            foreach (var kv in listenDict)
                                                existing[kv.Key] = kv.Value;
                                        }
                                        else
                                        {
                                            listensList[idx] = listenDict;
                                        }
                                    }
                                    else
                                    {
                                        // 补齐空位并追加
                                        while (listensList.Count < idx)
                                            listensList.Add(new Dictionary<string, object>());
                                        listensList.Add(listenDict);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 合并补丁失败时继续使用基础配置
                }

                var yaml = LyToYamlConverter.DictToYaml(appSettings);
                return Results.Json(new { success = true, yaml });
            }
            catch (LyConfigException ex)
            {
                return Results.Json(new { success = false, message = $"转换失败（语法错误）: {ex.Message}" }, statusCode: 400);
            }
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

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
                    totalConnections = ConnectionTracker.GetActiveCount(),
                    httpConnections = ConnectionTracker.GetHttpCount(),
                    wsConnections = ConnectionTracker.GetWebSocketCount(),
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

            return TypedResults.Ok(result);
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
