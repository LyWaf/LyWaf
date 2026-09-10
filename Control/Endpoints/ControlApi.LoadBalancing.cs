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
    private sealed record LbDestinationListItem(string Id, string Address, string Host, int Port, string Scheme,
        Dictionary<string, string> Metadata, string Source);
    private sealed record LbClusterListItem(string Id, string LoadBalancingPolicy, List<LbDestinationListItem> Destinations, int DestinationCount);
    private static void MapLoadBalancingEndpoints(WebApplication app, int controlPort)
    {
        MapDestinationListEndpoints(app, controlPort);
        // =============== 负载均衡管理 API ===============

        // 获取所有集群及其目标列表
        app.MapGet("/api/lb/clusters", (HttpContext ctx, IConfiguration config) =>
        {
            var rpSection = config.GetSection("ReverseProxy:Clusters");
            var clusters = new List<LbClusterListItem>();
            foreach (var clusterSection in rpSection.GetChildren())
            {
                var clusterId = clusterSection.Key;
                var policy = clusterSection["LoadBalancingPolicy"] ?? "RoundRobin";
                var destinations = new List<LbDestinationListItem>();
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
                    destinations.Add(new(dest.Key, address, uri?.Host ?? "*", uri?.Port ?? 0,
                        uri?.Scheme ?? "http", metadata, destSource == ConfigSource.PatchConfig ? "patch" : "original"));
                }
                clusters.Add(new(clusterId, policy, destinations, destinations.Count));
            }
            return Results.Ok(ManagementLists.From(clusters).Sort("id", item => item.Id)
                .Sort("loadBalancingPolicy", item => item.LoadBalancingPolicy).Sort("destinationCount", item => item.DestinationCount)
                .Search(item => item.Id + " " + item.LoadBalancingPolicy + " " + string.Join(' ', item.Destinations.Select(destination => destination.Address)))
                .Read(ctx.Request.Query, "id"));
        }).Produces<ManagementPage<LbClusterListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/lb/outliers", (HttpContext ctx) =>
        {
            var registry = ctx.RequestServices.GetRequiredService<OutlierDetectionRegistry>();
            return TypedResults.Ok(ManagementLists.From(registry.GetSnapshots()).Sort("clusterId", item => item.ClusterId)
                .Sort("destinationId", item => item.DestinationId).Sort("errorRate", item => item.ErrorRate)
                .Sort("ejectedUntil", item => item.EjectedUntil).Timestamp(item => item.EjectedUntil)
                .Search(item => $"{item.ClusterId} {item.DestinationId} {item.Address} {item.State} {item.Reason}")
                .Read(ctx.Request.Query, "clusterId"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/discovery/snapshots", (HttpContext ctx) =>
        {
            var registry = ctx.RequestServices.GetRequiredService<LyWaf.Services.Discovery.DiscoveryRegistry>();
            return TypedResults.Ok(ManagementLists.From(registry.GetAll()).Sort("clusterId", item => item.ClusterId)
                .Sort("providerId", item => item.ProviderId).Sort("observedAt", item => item.ObservedAt)
                .Timestamp(item => item.ObservedAt).Search(item => $"{item.ClusterId} {item.ProviderId} {item.Version}")
                .Read(ctx.Request.Query, "observedAt", true));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

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
            return TypedResults.Ok(new { success = true, policies });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 更新集群的负载均衡策略
        app.MapPost("/api/lb/policy/update", async (HttpContext ctx, IConfiguration config, UpdateClusterPolicyRequest request) =>
        {

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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加目标服务器
        app.MapPost("/api/lb/destinations/add", async (HttpContext ctx, IConfiguration config, AddDestinationRequest request) =>
        {

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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 编辑目标服务器
        app.MapPost("/api/lb/destinations/update", async (HttpContext ctx, IConfiguration config, UpdateDestinationRequest request) =>
        {

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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除目标服务器
        app.MapPost("/api/lb/destinations/remove", async (HttpContext ctx, IConfiguration config, RemoveDestinationRequest request) =>
        {

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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 批量删除目标服务器
        app.MapPost("/api/lb/destinations/batch-remove", RemoveDestinationsBatchAsync)
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除集群补丁（恢复为原始配置）
        app.MapPost("/api/lb/patch/remove", async (HttpContext ctx, IConfiguration config, RemoveClusterPatchRequest request) =>
        {

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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
