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
    private static void MapThrottlingEndpoints(WebApplication app, int controlPort)
    {
        // =============== 带宽限速管理 API ===============

        // 获取带宽限速配置
        app.MapGet("/api/throttle/config", (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            var config = speedLimitService.GetThrottleConfig();
            var options = speedLimitService.GetOptions();
            return Results.Json(new
            {
                success = true,
                enabled = options.Throttled.Enabled,
                global = config.Global,
                pathLimits = config.PathLimits,
                ipLimits = config.IpLimits,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 切换带宽限速启用状态
        app.MapPost("/api/throttle/toggle", (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            var options = speedLimitService.GetOptions();
            options.Throttled.Enabled = !options.Throttled.Enabled;
            return Results.Json(new
            {
                success = true,
                enabled = options.Throttled.Enabled,
                message = options.Throttled.Enabled ? "带宽限速已启用" : "带宽限速已禁用"
            });
        }).RequireHost($"*:{controlPort}");

        // 设置全局带宽限速
        app.MapPost("/api/throttle/global", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<SetGlobalThrottleRequest>();
                if (request == null)
                    return Results.Json(new { success = false, message = "请求无效" }, statusCode: 400);

                speedLimitService.SetGlobalThrottle(request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = request.LimitKbps > 0
                        ? $"已设置全局带宽限速: {request.LimitKbps} KB/s"
                        : "已关闭全局带宽限速",
                    global = request.LimitKbps
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 设置 IP 带宽限速
        app.MapPost("/api/throttle/ip/add", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                if (request.LimitKbps <= 0)
                {
                    return Results.Json(new { success = false, message = "限速值必须大于 0" }, statusCode: 400);
                }

                speedLimitService.SetIpThrottle(request.Ip, request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = $"已设置 IP {request.Ip} 带宽限速: {request.LimitKbps} KB/s",
                    ip = request.Ip,
                    limitKbps = request.LimitKbps,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 IP 带宽限速
        app.MapPost("/api/throttle/ip/remove", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                if (speedLimitService.RemoveIpThrottle(request.Ip))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 IP {request.Ip} 的带宽限速",
                        ip = request.Ip,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在限速列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 设置路径带宽限速
        app.MapPost("/api/throttle/path/add", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddPathThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (request.LimitKbps <= 0)
                {
                    return Results.Json(new { success = false, message = "限速值必须大于 0" }, statusCode: 400);
                }

                speedLimitService.SetPathThrottle(request.Path, request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = $"已设置路径 {request.Path} 带宽限速: {request.LimitKbps} KB/s",
                    path = request.Path,
                    limitKbps = request.LimitKbps,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除路径带宽限速
        app.MapPost("/api/throttle/path/remove", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemovePathThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (speedLimitService.RemovePathThrottle(request.Path))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除路径 {request.Path} 的带宽限速",
                        path = request.Path,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：路径不在限速列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 保存当前限速配置到补丁
        app.MapPost("/api/throttle/patch/save", async (HttpContext ctx, ISpeedLimitService speedLimitService, IConfiguration config) =>
        {
            try
            {
                var throttleConfig = speedLimitService.GetThrottleConfig();
                var options = speedLimitService.GetOptions();
                await _lbPatchLock.WaitAsync();
                try
                {
                    var patch = await ReadPatchFile();
                    var speedLimit = EnsurePatchSection(patch, "SpeedLimit");
                    var throttled = new Dictionary<string, object>
                    {
                        ["Enabled"] = options.Throttled.Enabled,
                        ["Global"] = throttleConfig.Global,
                        ["Everys"] = throttleConfig.PathLimits.ToDictionary(k => k.Key, k => (object)k.Value),
                        ["IpEverys"] = throttleConfig.IpLimits.ToDictionary(k => k.Key, k => (object)k.Value)
                    };
                    speedLimit["Throttled"] = throttled;
                    UpdatePatchSourceTracking(patch);
                    await SavePatchAndReload(patch, config);
                    return Results.Json(new { success = true, message = "限速配置已保存到补丁" });
                }
                finally
                {
                    _lbPatchLock.Release();
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除补丁中的限速配置（恢复为原始配置）
        app.MapPost("/api/throttle/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                await _lbPatchLock.WaitAsync();
                try
                {
                    var patch = await ReadPatchFile();
                    if (patch.Remove("SpeedLimit"))
                    {
                        UpdatePatchSourceTracking(patch);
                        await SavePatchAndReload(patch, config);
                        return Results.Json(new { success = true, message = "已删除补丁中的限速配置，恢复为原始配置" });
                    }
                    return Results.Json(new { success = false, message = "补丁中没有限速配置" }, statusCode: 404);
                }
                finally
                {
                    _lbPatchLock.Release();
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}

