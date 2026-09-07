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
    private static void MapInterceptEndpoints(WebApplication app, int controlPort)
    {
        // =============== 拦截事件 API ===============

        // 获取拦截事件
        app.MapGet("/api/intercept-events", (HttpContext ctx) =>
        {
            var ip = ctx.Request.Query["ip"].FirstOrDefault();
            var domain = ctx.Request.Query["domain"].FirstOrDefault();
            DateTime? startTime = DateTime.TryParse(ctx.Request.Query["startTime"].FirstOrDefault(), out var st) ? st : null;
            DateTime? endTime = DateTime.TryParse(ctx.Request.Query["endTime"].FirstOrDefault(), out var et) ? et : null;

            var events = SharedData.GetInterceptEvents(ip, domain, startTime, endTime);
            return Results.Json(new
            {
                success = true,
                count = events.Count,
                events = events.Select(e => new
                {
                    sourceIp = e.SourceIp,
                    region = e.Region,
                    city = e.City,
                    application = e.Application,
                    ruleName = e.RuleName,
                    ruleType = e.RuleType,
                    hitCount = e.HitCount,
                    duration = (int)(e.LastHitTime - e.FirstHitTime).TotalMinutes,
                    firstHitTime = e.FirstHitTime,
                    lastHitTime = e.LastHitTime,
                }),
            });
        }).RequireHost($"*:{controlPort}");

        // 清除拦截事件
        app.MapPost("/api/intercept-events/clear", (HttpContext ctx) =>
        {
            SharedData.ClearInterceptEvents();
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "清除拦截事件", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true, message = "拦截事件已清除" });
        }).RequireHost($"*:{controlPort}");

        // =============== 拦截明细日志 API ===============

        // 列出拦截日志文件
        app.MapGet("/api/intercept-logs/files", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var files = LogUtil.ListLogFiles(dir, "intercept_*.log");
            return Results.Json(new { success = true, files });
        }).RequireHost($"*:{controlPort}");

        // 读取拦截日志条目
        app.MapGet("/api/intercept-logs/entries", async (HttpContext ctx) =>
        {
            var fileName = ctx.Request.Query["file"].FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return Results.Json(new { success = false, message = "无效的文件名" });

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath))
                return Results.Json(new { success = false, message = $"文件不存在: {fileName}" });

            _ = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault() ?? "0", out var offset);
            _ = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault() ?? "20", out var limit);
            var search = ctx.Request.Query["search"].FirstOrDefault();
            DateTime? startTime = DateTime.TryParse(ctx.Request.Query["startTime"].FirstOrDefault(), out var st2) ? st2 : null;
            DateTime? endTime = DateTime.TryParse(ctx.Request.Query["endTime"].FirstOrDefault(), out var et2) ? et2 : null;

            var (entries, total) = await LogUtil.ParseLogFileAsync(filePath, offset, Math.Clamp(limit, 1, 100), search, startTime, endTime);
            return Results.Json(new
            {
                success = true,
                entries,
                total,
                offset,
                limit,
                fileName,
            });
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

    }
}

