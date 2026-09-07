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
    private static void MapBlockedIpEndpoints(WebApplication app, int controlPort)
    {
        // =============== 被封禁 IP 管理 API ===============
        
        // 获取被封禁的 IP 列表
        app.MapGet("/api/blocked-ips", (HttpContext ctx, IRuntimeStateStore runtimeState) =>
        {
            var stateExpirations = runtimeState.GetSnapshots()
                .ToDictionary(x => $"{x.Domain}\n{x.Key}", x => x.ExpiresAt, StringComparer.Ordinal);
            var blockedIps = SharedData.ClientFb.GetValidItemsWithExpiry()
                .Select(x => new
                {
                    ip = x.Key,
                    type = "blocked",
                    reason = x.Value,
                    remainingSeconds = x.RemainingTime?.TotalSeconds,
                    expiresAt = SharedData.ClientFb.GetExpiration(x.Key)
                })
                .Concat(SharedData.CaptchaPending.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "captcha",
                        reason = $"验证码待验证: {x.Value.RuleName}",
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        expiresAt = SharedData.CaptchaPending.GetExpiration(x.Key)
                    }))
                .Concat(SharedData.ClientThrottled.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "throttled",
                        reason = $"带宽限速: {x.Value.EveryCapacity / 1024}KB/s",
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        expiresAt = SharedData.ClientThrottled.GetExpiration(x.Key)
                    }))
                .Concat(SharedData.IpLogTargets.GetValidItemsWithExpiry()
                    .Select(x =>
                    {
                        var (exists, sizeBytes, _) = IpRequestLogger.GetLogFileInfo(x.Key);
                        return new
                        {
                            ip = x.Key,
                            type = "log",
                            reason = exists ? $"请求日志记录中 ({sizeBytes / 1024}KB)" : "请求日志记录中",
                            remainingSeconds = x.RemainingTime?.TotalSeconds,
                            expiresAt = SharedData.IpLogTargets.GetExpiration(x.Key)
                        };
                    }))
                .Concat(runtimeState.GetValues<string>("ip.blocked").Select(x =>
                {
                    var expiresAt = stateExpirations.GetValueOrDefault($"ip.blocked\n{x.Key}");
                    return new
                    {
                        ip = x.Key, type = "blocked", reason = x.Value,
                        remainingSeconds = expiresAt.HasValue ? (expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds : (double?)null,
                        expiresAt = expiresAt?.LocalDateTime
                    };
                }))
                .Concat(runtimeState.GetValues<CaptchaPendingInfo>("captcha.pending").Select(x =>
                {
                    var expiresAt = stateExpirations.GetValueOrDefault($"captcha.pending\n{x.Key}");
                    return new
                    {
                        ip = x.Key, type = "captcha", reason = $"验证码待验证: {x.Value.RuleName}",
                        remainingSeconds = expiresAt.HasValue ? (expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds : (double?)null,
                        expiresAt = expiresAt?.LocalDateTime
                    };
                }))
                .GroupBy(x => $"{x.type}\n{x.ip}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
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

        // 手动添加 IP 限制（支持 blocked/captcha/throttled/log 类型）
        app.MapPost("/api/blocked-ips/add", async (HttpContext ctx, IRuntimeStateStore runtimeState, ICaptchaService captchaService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<BlockIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var duration = request.Duration ?? TimeSpan.FromMinutes(10);
                var reason = request.Reason ?? "手动封禁";
                var type = request.Type ?? "blocked";
                string message;

                switch (type)
                {
                    case "captcha":
                        var captchaInfo = new CaptchaPendingInfo
                        {
                            RuleName = reason.Length > 0 ? reason : "手动验证码",
                            ActionSeconds = (int)duration.TotalSeconds,
                            CreatedAt = DateTime.UtcNow
                        };
                        captchaService.SetPending(ip, captchaInfo.RuleName, captchaInfo.ActionSeconds);
                        message = $"已对 IP 启用验证码: {ip}";
                        break;

                    case "throttled":
                        var speedKBps = request.SpeedLimit > 0 ? request.SpeedLimit : 100;
                        var throttleLimit = new ClientThrottledLimit
                        {
                            EveryCapacity = speedKBps * 1024,
                            LeftToken = speedKBps * 1024,
                        };
                        SharedData.ClientThrottled.Set(ip, throttleLimit, duration);
                        runtimeState.Set("ip.throttled", ip, throttleLimit, duration);
                        message = $"已对 IP 启用限速: {ip} ({speedKBps}KB/s)";
                        break;

                    case "log":
                        SharedData.IpLogTargets.Set(ip, DateTime.Now, duration);
                        runtimeState.Set("ip.log", ip, DateTimeOffset.UtcNow, duration);
                        message = $"已开始记录 IP: {ip} 的请求日志 ({duration.TotalMinutes:0}分钟)";
                        break;

                    default: // "blocked"
                        SharedData.ClientFb.Set(ip, reason, duration);
                        runtimeState.Set("ip.blocked", ip, reason, duration);
                        message = $"已封禁 IP: {ip}";
                        break;
                }

                return Results.Json(new
                {
                    success = true,
                    message = message,
                    ip = ip,
                    type = type,
                    reason = reason,
                    duration = duration.ToString(),
                    expiresAt = DateTime.Now.Add(duration),
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 解封 IP
        app.MapPost("/api/blocked-ips/remove", async (HttpContext ctx, IRuntimeStateStore runtimeState) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UnblockIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var removed = SharedData.ClientFb.Remove(ip);
                removed |= SharedData.CaptchaPending.Remove(ip);
                removed |= SharedData.ClientThrottled.Remove(ip);
                removed |= SharedData.IpLogTargets.Remove(ip);
                removed |= runtimeState.Remove("ip.blocked", ip);
                removed |= runtimeState.Remove("captcha.pending", ip);
                removed |= runtimeState.Remove("ip.throttled", ip);
                removed |= runtimeState.Remove("ip.log", ip);

                if (removed)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已解封 IP: {ip}",
                        ip = ip,
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
        app.MapPost("/api/blocked-ips/clear", (HttpContext ctx, IRuntimeStateStore runtimeState) =>
        {
            var count = SharedData.ClientFb.Count + SharedData.CaptchaPending.Count + SharedData.ClientThrottled.Count + SharedData.IpLogTargets.Count;
            SharedData.ClientFb.Clear();
            SharedData.CaptchaPending.Clear();
            SharedData.ClientThrottled.Clear();
            SharedData.IpLogTargets.Clear();
            foreach (var domain in new[] { "ip.blocked", "captcha.pending", "ip.throttled", "ip.log" })
                foreach (var state in runtimeState.GetSnapshots(domain)) runtimeState.Remove(domain, state.Key);
            return Results.Json(new
            {
                success = true,
                message = $"已清空 {count} 个被封禁的 IP",
                clearedCount = count,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

    }
}

