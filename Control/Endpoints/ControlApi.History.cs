using System.Collections.Concurrent;
using LyWaf.Config;
using LyWaf.Plugins.Packaging;
using LyWaf.Services.Acme;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using LyWaf.Services.Configuration;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Shared;
using LyWaf.Utils;

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LyWaf.Middleware;
using LyWaf.Plugins;
using LyWaf.Plugins.Core;
using LyWaf.Services.WafInfo;
using LyWaf.Services;
using LyWaf.Services.Metrics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapHistoryEndpoints(WebApplication app, int controlPort)
    {
        // =============== DuckDB 历史查询 API ===============

        app.MapGet("/api/traffic/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var granularity = ctx.Request.Query["granularity"].FirstOrDefault() ?? "hour";

                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-1);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetTrafficHistory(from, to, granularity);
                return Results.Json(new { success = true, data, from, to, granularity });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/security/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-7);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetSecurityTimeSlots(from, to);
                return Results.Json(new
                {
                    success = true,
                    data = data.Select(s => new
                    {
                        time = s.Time,
                        wafIntercept = s.WafIntercept,
                        blacklistBlock = s.BlacklistBlock,
                        ccAttack = s.CcAttack,
                        crawlerDetect = s.CrawlerDetect,
                        geoBlock = s.GeoBlock,
                        rateLimit = s.RateLimit,
                        total = s.Total,
                    }),
                    from, to,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/timing/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var pathFilter = ctx.Request.Query["path"].FirstOrDefault();
                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-1);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetApiTimingHistory(from, to, pathFilter);
                return Results.Json(new
                {
                    success = true,
                    data = data.Select(item => new
                    {
                        snapshotTime = item.SnapshotTime,
                        path = item.Path,
                        method = item.Method,
                        backend = item.Backend,
                        originalHost = item.OriginalHost,
                        requestCount = item.RequestCount,
                        avgTotalTime = item.RequestCount > 0 ? Math.Round((double)item.TotalTime / item.RequestCount, 2) : 0,
                        avgBackendTime = item.RequestCount > 0 ? Math.Round((double)item.BackendTime / item.RequestCount, 2) : 0,
                        minTotalTime = item.MinTotalTime,
                        maxTotalTime = item.MaxTotalTime,
                        statusCodeCounts = item.StatusCodeCounts,
                    }),
                    from, to,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/stats/endpoint-history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var statType = ctx.Request.Query["type"].FirstOrDefault() ?? "dest";
                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var topN = int.TryParse(ctx.Request.Query["topN"].FirstOrDefault(), out var n) ? n : 50;
                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-1);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetEndpointStatsHistory(statType, from, to, topN);
                return Results.Json(new { success = true, data, from, to, statType });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}
