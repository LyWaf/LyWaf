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
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record IpMonitoringResponse(bool Success, string Message, string Ip, DateTime Timestamp);
    private sealed record IpLogDeletedResponse(bool Success, string Message, DateTime Timestamp);
    private sealed record IpLogPage : ManagementPage<LogEntry>
    {
        public long FileSize { get; }
        public IpLogPage(ManagementPage<LogEntry> page, long fileSize)
            : base(page.Success, page.Data, page.Total, page.Page, page.PageSize, page.SortBy, page.SortDirection)
            => FileSize = fileSize;
    }

    private static void MapIpLogEndpoints(WebApplication app, int controlPort)
    {
        // =============== IP 请求日志 API ===============

        // 获取当前正在监控的 IP 列表
        app.MapGet("/api/ip-log/list", (HttpContext ctx) =>
        {
            var targets = SharedData.IpLogTargets.GetValidItemsWithExpiry()
                .Select(x =>
                {
                    var (exists, sizeBytes, lastWriteTime) = IpRequestLogger.GetLogFileInfo(x.Key);
                    return new
                    {
                        ip = x.Key,
                        addedAt = x.Value,
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        logFileExists = exists,
                        logFileSize = sizeBytes,
                        lastWriteTime = lastWriteTime,
                    };
                })
                .OrderBy(x => x.ip)
                .ToList();

            return TypedResults.Ok(ManagementLists.From(targets)
                .Sort("ip", item => item.ip)
                .Sort("addedAt", item => item.addedAt)
                .Sort("logFileSize", item => item.logFileSize)
                .Search(item => item.ip)
                .Timestamp(item => new DateTimeOffset(item.addedAt))
                .Read(ctx.Request.Query, "ip"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加 IP 到请求日志监控
        app.MapPost("/api/ip-log/add", (HttpContext ctx, IpLogRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var duration = request.Duration ?? TimeSpan.FromMinutes(10);
                SharedData.IpLogTargets.Set(ip, DateTime.Now, duration);

                return Results.Json(new
                {
                    success = true,
                    message = $"已开始记录 IP: {ip} 的请求日志",
                    ip = ip,
                    timestamp = DateTime.Now
                });
        }).Produces<IpMonitoringResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 停止 IP 请求日志监控
        app.MapPost("/api/ip-log/remove", (HttpContext ctx, IpLogRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var removed = SharedData.IpLogTargets.Remove(ip);

                return Results.Json(new
                {
                    success = removed,
                    message = removed ? $"已停止记录 IP: {ip}" : "IP 不在监控列表中",
                    ip = ip,
                    timestamp = DateTime.Now
                }, statusCode: removed ? 200 : 404);
        }).Produces<IpMonitoringResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 读取 IP 的请求日志（分页，按条目解析）
        app.MapGet("/api/ip-log/read/{ip}", async (HttpContext ctx, string ip) =>
        {
            if (!System.Net.IPAddress.TryParse(ip, out _)) throw new ArgumentException("IP 地址无效", "ip");
            var query = ManagementListQuery.Parse(ctx.Request.Query, ["time"], "time", descending: true);
            var path = IpRequestLogger.GetLogFilePath(ip);
            if (!File.Exists(path)) return TypedResults.Ok(new IpLogPage(query.Result<LogEntry>([], 0), 0));
            using var scanner = new ReverseLogScanner(path);
            var size = new FileInfo(path).Length;
            return TypedResults.Ok(new IpLogPage(await scanner.QueryAsync(query, ctx.RequestAborted), size));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除 IP 的日志文件
        app.MapPost("/api/ip-log/delete-file", (HttpContext ctx, IpLogRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var deleted = IpRequestLogger.DeleteLog(ip);

                return Results.Json(new
                {
                    success = deleted,
                    message = deleted ? $"已删除 {ip} 的日志文件" : "日志文件不存在",
                    timestamp = DateTime.Now
                }, statusCode: deleted ? 200 : 404);
        }).Produces<IpLogDeletedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
