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
    private sealed record TimingPage<T, TSummary, TBackend> : ManagementPage<T>
    {
        public TSummary Summary { get; }
        public TBackend BackendStats { get; }
        public TimingPage(ManagementPage<T> page, TSummary summary, TBackend backends)
            : base(page.Success, page.Data, page.Total, page.Page, page.PageSize, page.SortBy, page.SortDirection)
            => (Summary, BackendStats) = (summary, backends);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<TimingPage<T, TSummary, TBackend>> TimingResult<T, TSummary, TBackend>(ManagementPage<T> page, TSummary summary, TBackend backends)
        => TypedResults.Ok(new TimingPage<T, TSummary, TBackend>(page, summary, backends));

    private static void MapInterceptEndpoints(WebApplication app, int controlPort)
    {
        // =============== 拦截事件 API ===============

        // 获取拦截事件
        app.MapGet("/api/intercept-events", (HttpContext ctx) =>
        {
            var ip = ctx.Request.Query["ip"].FirstOrDefault();
            var domain = ctx.Request.Query["domain"].FirstOrDefault();
            var events = SharedData.GetInterceptEvents(ip, domain);
            var data = events.Select(e => new
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
                });
            return TypedResults.Ok(ManagementLists.From(data)
                .Sort("lastHitTime", item => item.lastHitTime)
                .Sort("firstHitTime", item => item.firstHitTime)
                .Sort("sourceIp", item => item.sourceIp).Sort("hitCount", item => item.hitCount)
                .Search(item => $"{item.sourceIp} {item.application} {item.ruleName} {item.ruleType} {item.region} {item.city}")
                .Timestamp(item => new DateTimeOffset(item.lastHitTime))
                .Read(ctx.Request.Query, "lastHitTime", descending: true));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 清除拦截事件
        app.MapPost("/api/intercept-events/clear", (HttpContext ctx) =>
        {
            SharedData.ClearInterceptEvents();
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "清除拦截事件", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true, message = "拦截事件已清除" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 拦截明细日志 API ===============

        // 列出拦截日志文件
        app.MapGet("/api/intercept-logs/files", (HttpContext ctx) =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var files = LogUtil.ListLogFiles(dir, "intercept_*.log");
            return TypedResults.Ok(ManagementLists.From(files)
                .Sort("name", item => item.Name).Sort("size", item => item.Size)
                .Sort("modifiedAt", item => item.ModifiedAt)
                .Search(item => item.Name).Timestamp(item => item.ModifiedAt)
                .Read(ctx.Request.Query, "modifiedAt", descending: true));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 读取拦截日志条目
        app.MapGet("/api/intercept-logs/entries", async (HttpContext ctx) =>
        {
            return await QueryRequestLog(ctx, "intercept_");
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // API 耗时统计数据列表
        app.MapGet("/api/timing/list", (HttpContext ctx) =>
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
                
                var filter = TimingListFilter.Parse(ctx.Request.Query);
                var page = ManagementLists.From(data.Where(item => filter.Matches(item.method, item.backend, item.requestCount, item.avgTotalTime)))
                    .Sort("path", item => item.path).Sort("method", item => item.method)
                    .Sort("backend", item => item.backend).Sort("requestCount", item => item.requestCount)
                    .Sort("avgTotalTime", item => item.avgTotalTime).Sort("avgBackendTime", item => item.avgBackendTime)
                    .Sort("errorRate", item => item.errorRate).Sort("lastRequestTime", item => item.lastRequestTime)
                    .Search(item => $"{item.path} {item.method} {item.backend} {item.originalHost}")
                    .Timestamp(item => new DateTimeOffset(item.lastRequestTime))
                    .Read(ctx.Request.Query, "requestCount", descending: true);
                return TimingResult(page, new
                    {
                        totalApis,
                        apiCount = totalApis,
                        totalRequests,
                        avgTotalTime,
                        avgBackendTime,
                        avgGatewayTime,
                        errorRate = totalRequests == 0 ? 0 : data.Sum(item => item.statusCodeCounts.Where(status => status.Key >= 400).Sum(status => (long)status.Value)) / (double)totalRequests
                    }, backendStats);
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 清除 API 耗时统计数据
        app.MapPost("/api/timing/clear", (HttpContext ctx) =>
        {
                SharedData.ApiTimings.Clear();
                return TypedResults.Ok(new { success = true, message = "API 耗时统计数据已清除" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 重置流量统计数据
        app.MapPost("/api/traffic/reset", (HttpContext ctx) =>
        {
                SharedData.Traffic.Reset();
                SharedData.GeoTraffic.Reset();
                return TypedResults.Ok(new { success = true, message = "流量统计数据已重置" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
