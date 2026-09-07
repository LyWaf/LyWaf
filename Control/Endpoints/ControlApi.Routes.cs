using System.Diagnostics;
using System.Text;
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
using System.Security.Cryptography;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.RuntimeState;
using LyWaf.Services;
using LyWaf.Policy;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapRouteEndpoints(WebApplication app, int controlPort)
    {
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
                    {
                        routePatch["Order"] = request.Order.Value;
                    }
                    else
                    {
                        // 自动分配 Order：从 1000 开始，取所有已有路由 Order 的最大值 +1
                        var usedOrders = new HashSet<int>();
                        foreach (var rs in config.GetSection("ReverseProxy:Routes").GetChildren())
                        {
                            if (int.TryParse(rs["Order"], out var o)) usedOrders.Add(o);
                        }
                        // 也检查补丁中已有路由的 Order
                        foreach (var kv in routes)
                        {
                            if (kv.Value is Dictionary<string, object> rDict && rDict.TryGetValue("Order", out var oVal))
                            {
                                if (oVal is int oi) usedOrders.Add(oi);
                                else if (oVal is string os && int.TryParse(os, out var op)) usedOrders.Add(op);
                            }
                        }
                        var nextOrder = 1000;
                        while (usedOrders.Contains(nextOrder)) nextOrder++;
                        routePatch["Order"] = nextOrder;
                    }

                    var matchPatch = new Dictionary<string, object>();
                    if (request.Match != null)
                    {
                        if (request.Match.Path != null)
                            matchPatch["Path"] = request.Match.Path;
                        if (request.Match.Hosts != null && request.Match.Hosts.Count > 0)
                            matchPatch["Hosts"] = request.Match.Hosts.ToList<object>();
                        if (request.Match.Methods != null && request.Match.Methods.Count > 0)
                            matchPatch["Methods"] = request.Match.Methods.ToList<object>();
                    }

                    // 保证路由必须有 Hosts：若未指定，则尝试从 routeId 中提取端口号生成默认 *:port
                    if (!matchPatch.ContainsKey("Hosts"))
                    {
                        var defaultPort = ExtractPortFromRouteId(request.RouteId);
                        if (defaultPort.HasValue)
                        {
                            matchPatch["Hosts"] = new List<object> { $"*:{defaultPort.Value}" };
                        }
                    }

                    if (matchPatch.Count > 0)
                        routePatch["Match"] = matchPatch;

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

                    // 保证路由有 Hosts：若 Match 中无 Hosts，则从 routeId 提取端口号生成默认 *:port
                    if (routePatch.TryGetValue("Match", out var finalMatchObj) && finalMatchObj is Dictionary<string, object> finalMatch)
                    {
                        if (!finalMatch.ContainsKey("Hosts"))
                        {
                            var defaultPort = ExtractPortFromRouteId(request.RouteId);
                            if (defaultPort.HasValue)
                            {
                                finalMatch["Hosts"] = new List<object> { $"*:{defaultPort.Value}" };
                            }
                        }
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

    }
}

