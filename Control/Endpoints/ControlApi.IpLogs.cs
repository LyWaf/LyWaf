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

            return Results.Json(new
            {
                success = true,
                count = targets.Count,
                targets = targets,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 IP 到请求日志监控
        app.MapPost("/api/ip-log/add", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<IpLogRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 停止 IP 请求日志监控
        app.MapPost("/api/ip-log/remove", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<IpLogRequest>();
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
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 读取 IP 的请求日志（分页，按条目解析）
        app.MapGet("/api/ip-log/read/{ip}", async (HttpContext ctx, string ip, int offset = 0, int limit = 50) =>
        {
            var (exists, sizeBytes, lastWriteTime) = IpRequestLogger.GetLogFileInfo(ip);
            if (!exists)
            {
                return Results.Json(new
                {
                    success = true,
                    ip = ip,
                    entries = Array.Empty<object>(),
                    total = 0,
                    fileSize = 0L,
                    timestamp = DateTime.Now
                });
            }

            var entries = await IpRequestLogger.ReadEntriesAsync(ip, offset, limit);
            var total = await IpRequestLogger.CountEntriesAsync(ip);

            return Results.Json(new
            {
                success = true,
                ip = ip,
                entries = entries,
                total = total,
                offset = offset,
                limit = limit,
                fileSize = sizeBytes,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 删除 IP 的日志文件
        app.MapPost("/api/ip-log/delete-file", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<IpLogRequest>();
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
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}

