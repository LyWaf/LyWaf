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
    private sealed record ServiceReloadResponse(bool Success, string Message, bool PortsChanged, bool RestartRequired,
        ConfigurationChangeImpact ChangeImpact, DateTime Timestamp);
    private sealed record ReadinessResponse(bool Success, LyWaf.Services.Lifecycle.DrainSnapshot State);
    private static void MapOverviewEndpoints(WebApplication app, int controlPort, WafInfoOptions wafInfos)
    {
        // =============== 配置概览 API ===============
        app.MapGet("/api/overview", (HttpContext ctx, IConfiguration config) =>
        {
                // 控制台监听
                var cl = wafInfos.GetControlListen();

                // 证书信息
                var certs = wafInfos.Certs.Select(c => new
                {
                    host = c.Host,
                    pemFile = Path.GetFileName(c.PemFile),
                    hasKey = !string.IsNullOrEmpty(c.KeyFile)
                }).ToList();

                // 收集所有路由信息（含 hosts 和 source）
                var rpRoutes = config.GetSection("ReverseProxy:Routes");
                var allRoutes = new List<(string RouteId, string ClusterId, string Path, List<string> Hosts, string Source)>();
                foreach (var rs in rpRoutes.GetChildren())
                {
                    var matchSection = rs.GetSection("Match");
                    var hosts = new List<string>();
                    foreach (var h in matchSection.GetSection("Hosts").GetChildren())
                    {
                        if (h.Value != null) hosts.Add(h.Value);
                    }
                    var source = DetectConfigSource(config, $"ReverseProxy:Routes:{rs.Key}");
                    var sourceText = source == ConfigSource.PatchConfig ? "patch" : "original";
                    allRoutes.Add((rs.Key, rs["ClusterId"] ?? "", matchSection["Path"] ?? "", hosts, sourceText));
                }

                // 收集所有集群信息
                var rpClusters = config.GetSection("ReverseProxy:Clusters");
                var allClusters = new Dictionary<string, object>();
                foreach (var cs in rpClusters.GetChildren())
                {
                    var destinations = new List<object>();
                    foreach (var dest in cs.GetSection("Destinations").GetChildren())
                    {
                        destinations.Add(new
                        {
                            id = dest.Key,
                            address = dest["Address"] ?? ""
                        });
                    }
                    allClusters[cs.Key] = new
                    {
                        id = cs.Key,
                        policy = cs["LoadBalancingPolicy"] ?? "RoundRobin",
                        destinationCount = destinations.Count,
                        destinations
                    };
                }

                // 辅助方法：解析 host 模式中的端口
                static int? ExtractPort(string pattern)
                {
                    if (string.IsNullOrEmpty(pattern)) return null;
                    // IPv6 格式 [::1]:8080
                    if (pattern.StartsWith('['))
                    {
                        var bracketEnd = pattern.IndexOf(']');
                        if (bracketEnd >= 0 && bracketEnd + 1 < pattern.Length && pattern[bracketEnd + 1] == ':')
                        {
                            if (int.TryParse(pattern[(bracketEnd + 2)..], out var p)) return p;
                        }
                        return null;
                    }
                    // 普通格式 host:port
                    var colonIdx = pattern.LastIndexOf(':');
                    if (colonIdx > 0 && int.TryParse(pattern[(colonIdx + 1)..], out var port))
                    {
                        return port;
                    }
                    return null;
                }

                // 判断路由是否匹配某个监听端口
                static bool RouteMatchesPort(string routeId, List<string> routeHosts, int listenPort)
                {
                    if (routeHosts.Count == 0)
                    {
                        // 从路由 ID 中提取 listen_XXXX_default 的端口号（兼容 simpleres_listen_XXXX_default 等前缀）
                        var listenIdx = routeId.IndexOf("listen_", StringComparison.Ordinal);
                        if (listenIdx >= 0 && routeId.EndsWith("_default"))
                        {
                            var portStr = routeId[(listenIdx + "listen_".Length)..^"_default".Length];
                            if (int.TryParse(portStr, out var routePort))
                                return routePort == listenPort;
                        }
                        // 其他无 Hosts 限制的路由匹配所有端口
                        return true;
                    }
                    foreach (var h in routeHosts)
                    {
                        var p = ExtractPort(h);
                        if (p == listenPort) return true;
                    }
                    return false;
                }

                // 确定路由的服务类型
                static string GetServiceType(string routeId)
                {
                    if (routeId.StartsWith("fileserver_", StringComparison.OrdinalIgnoreCase)) return "fileserver";
                    if (routeId.StartsWith("simpleres_", StringComparison.OrdinalIgnoreCase)) return "simpleres";
                    return "proxy";
                }

                // 读取补丁中的监听端口（包含尚未重启生效的新端口）
                var patchListenIndices = new HashSet<string>();
                var pendingPatchListens = new List<(string Host, int Port, bool IsHttps, int? AutoHttpsPort)>();
                try
                {
                    var patchPath = LbPatchConfig.GetPatchFilePath();
                    if (File.Exists(patchPath))
                    {
                        var patchJson = File.ReadAllText(patchPath, Encoding.UTF8);
                        using var patchDoc = System.Text.Json.JsonDocument.Parse(patchJson);
                        if (patchDoc.RootElement.TryGetProperty("Listens", out var patchListensEl))
                        {
                            foreach (var prop in patchListensEl.EnumerateObject())
                            {
                                patchListenIndices.Add(prop.Name);
                                // 索引 >= wafInfos.Listens.Count 表示是新增的、尚未重启生效的端口
                                if (int.TryParse(prop.Name, out var pIdx) && pIdx >= wafInfos.Listens.Count)
                                {
                                    var pHost = prop.Value.TryGetProperty("Host", out var hEl) ? hEl.GetString() ?? "0.0.0.0" : "0.0.0.0";
                                    var pPort = prop.Value.TryGetProperty("Port", out var pEl) ? pEl.GetInt32() : 0;
                                    var pHttps = prop.Value.TryGetProperty("IsHttps", out var sEl) && sEl.GetBoolean();
                                    int? pAutoHttps = prop.Value.TryGetProperty("AutoHttpsPort", out var aEl) && aEl.ValueKind == System.Text.Json.JsonValueKind.Number
                                        ? aEl.GetInt32() : null;
                                    if (pPort > 0) pendingPatchListens.Add((pHost, pPort, pHttps, pAutoHttps));
                                }
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                // 为每个监听端口匹配关联的路由和服务
                var listens = wafInfos.Listens.Select((l, idx) =>
                {
                    var boundRoutes = allRoutes
                        .Where(r => RouteMatchesPort(r.RouteId, r.Hosts, l.Port))
                        .Select(r => new
                        {
                            routeId = r.RouteId,
                            clusterId = r.ClusterId,
                            path = r.Path,
                            hosts = r.Hosts,
                            serviceType = GetServiceType(r.RouteId),
                            source = r.Source
                        })
                        .ToList();

                    return new
                    {
                        host = l.Host,
                        port = l.Port,
                        isHttps = l.IsHttps,
                        autoHttpsPort = l.AutoHttpsPort,
                        routes = boundRoutes,
                        source = patchListenIndices.Contains(idx.ToString()) ? "patch" : "original"
                    };
                }).ToList();

                // 追加尚未重启生效的补丁端口（已在补丁中但不在 wafInfos.Listens 中）
                foreach (var pl in pendingPatchListens)
                {
                    var boundRoutes = allRoutes
                        .Where(r => RouteMatchesPort(r.RouteId, r.Hosts, pl.Port))
                        .Select(r => new
                        {
                            routeId = r.RouteId,
                            clusterId = r.ClusterId,
                            path = r.Path,
                            hosts = r.Hosts,
                            serviceType = GetServiceType(r.RouteId),
                            source = r.Source
                        })
                        .ToList();

                    listens.Add(new
                    {
                        host = pl.Host,
                        port = pl.Port,
                        isHttps = pl.IsHttps,
                        autoHttpsPort = pl.AutoHttpsPort,
                        routes = boundRoutes,
                        source = "patch"
                    });
                }

                return TypedResults.Ok(new
                {
                    success = true,
                    listens,
                    controlListen = new { host = cl.Host, port = cl.Port },
                    certs,
                    clusters = allClusters.Values.ToList(),
                    timestamp = DateTime.Now
                });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

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
        }).Produces<string>(200, "text/yaml").RequireHost($"*:{controlPort}").RequireManagementPermission();
        
        app.MapPost("/api/stop", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            // 停止应用会触发插件系统停止（包括 AnalysisPlugin）
            lifetime.StopApplication();
            return TypedResults.Ok(new { message = "服务正在停止..." });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();
        
        app.MapPost("/api/reload", (HttpContext ctx, IConfiguration config) =>
        {
            if (config is not IConfigurationRoot configRoot)
            {
                return Results.Json(new { success = false, message = "配置重载失败：不支持的配置类型" }, statusCode: 500);
            }

            var lifecycle = ctx.RequestServices.GetRequiredService<ConfigurationLifecycleService>();
            var before = lifecycle.Capture(configRoot);
            ConfigurationChangeImpact impact;
            try { impact = lifecycle.ReloadAndValidate(configRoot, before); }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"配置重载冒烟检查失败: {ex.Message}" }, statusCode: 400);
            }

            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "重载配置", ctx.Connection.RemoteIpAddress?.ToString() ?? "");

            // 检测端口是否变化（仅通知前端，由用户确认后调用 /api/restart 重启）
            var newWafInfos = new WafInfoOptions();
            config.GetSection("WafInfos").Bind(newWafInfos);
            var oldPorts = wafInfos.Listens.Select(l => (l.Host, l.Port, l.IsHttps)).OrderBy(x => x).ToList();
            var newPorts = newWafInfos.Listens.Select(l => (l.Host, l.Port, l.IsHttps)).OrderBy(x => x).ToList();
            var portsChanged = !oldPorts.SequenceEqual(newPorts);

            return Results.Json(new
            {
                success = true,
                message = portsChanged
                    ? "配置已重新加载，检测到端口变更，需要重启服务才能生效"
                    : "配置已重新加载",
                portsChanged,
                restartRequired = impact.RequiresRestart,
                changeImpact = impact,
                timestamp = DateTime.Now
            });
        }).Produces<ServiceReloadResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/health/ready", (HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<LyWaf.Services.Lifecycle.GracefulShutdownCoordinator>();
            var state = coordinator.Snapshot();
            return Results.Json(new { success = state.Ready, state }, statusCode: state.Ready ? 200 : 503);
        }).Produces<ReadinessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/openapi.json", (EndpointDataSource endpoints) =>
            TypedResults.Ok(ManagementApiContract.BuildOpenApiDocument(endpoints)))
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/lifecycle/drain", (HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<LyWaf.Services.Lifecycle.GracefulShutdownCoordinator>();
            return TypedResults.Ok(new { success = true, state = coordinator.Snapshot() });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/upgrade/releases", async (HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<LyWaf.Services.Lifecycle.UpgradePackageService>();
            return TypedResults.Ok(ManagementLists.From(await service.ListAsync(ctx.RequestAborted))
                .Sort("version", item => item.Version).Sort("stagedAt", item => item.StagedAt)
                .Sort("status", item => item.Status).Search(item => $"{item.Version} {item.Status} {item.EntryPoint}")
                .Timestamp(item => item.StagedAt).Read(ctx.Request.Query, "stagedAt", descending: true));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/upgrade/pointers", async (HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<LyWaf.Services.Lifecycle.UpgradePackageService>();
            return TypedResults.Ok(await service.GetPointersAsync(ctx.RequestAborted));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/upgrade/stage", EnqueueUpgradeStage)
            .Produces<LyWaf.Services.ManagementTasks.ManagementJobResponse>(202)
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        MapUpgradeActivationEndpoints(app, controlPort);

        // 重启服务：先从就绪检查摘流，再等待现有 HTTP、下载和 WebSocket 排空。
        app.MapPost("/api/restart", (HttpContext ctx, IHostApplicationLifetime lifetime, IConfiguration config) =>
        {
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "重启服务", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            SharedData.RestartRequested = true;
            var timeoutSeconds = Math.Clamp(config.GetValue("Lifecycle:DrainTimeoutSeconds", 30), 1, 600);
            ScheduleDrainAndStop(ctx, lifetime, config);
            return TypedResults.Ok(new
            {
                success = true,
                message = "实例将先摘流并等待在途请求完成，然后重启",
                drainTimeoutSeconds = timeoutSeconds,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
