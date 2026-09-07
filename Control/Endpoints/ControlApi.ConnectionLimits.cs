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
    private static void MapConnectionLimitEndpoints(WebApplication app, int controlPort)
    {
        // =============== 功能状态切换 API ===============
        
        // 切换 IP 访问控制状态
        app.MapPost("/api/feature/ip-control/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().IpControl.Enabled;
                accessControlService.SetIpControlEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "ip-control",
                    enabled = enabled,
                    message = enabled ? "IP 访问控制已启用" : "IP 访问控制已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换地理位置访问控制状态
        app.MapPost("/api/feature/geo-control/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().GeoControl.Enabled;
                accessControlService.SetGeoControlEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "geo-control",
                    enabled = enabled,
                    message = enabled ? "地理位置访问控制已启用" : "地理位置访问控制已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // ==================== 连接限制 API ====================

        // 获取连接限制配置
        app.MapGet("/api/connection-limit/config", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var config = options.ConnectionLimit;
            var stats = accessControlService.GetConnectionStats();
            return Results.Json(new
            {
                success = true,
                enabled = config.Enabled,
                maxConnectionsPerIp = config.MaxConnectionsPerIp,
                maxConnectionsPerDestination = config.MaxConnectionsPerDestination,
                maxTotalConnections = config.MaxTotalConnections,
                rejectStatusCode = config.RejectStatusCode,
                pathLimits = config.PathLimits,
                stats = new
                {
                    totalConnections = stats.TotalConnections,
                    connectionsPerIp = stats.ConnectionsPerIp.Count,
                    connectionsPerDestination = stats.ConnectionsPerDestination.Count
                }
            });
        }).RequireHost($"*:{controlPort}");

        // 切换连接限制启用状态
        app.MapPost("/api/connection-limit/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().ConnectionLimit.Enabled;
                accessControlService.SetConnectionLimitEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    enabled,
                    message = enabled ? "连接限制已启用" : "连接限制已禁用"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新连接限制配置
        app.MapPost("/api/connection-limit/update", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateConnectionLimitRequest>();
                if (request == null) return Results.Json(new { success = false, message = "无效请求" });
                accessControlService.SetConnectionLimitConfig(
                    request.MaxConnectionsPerIp,
                    request.MaxConnectionsPerDestination,
                    request.MaxTotalConnections,
                    request.RejectStatusCode);
                return Results.Json(new { success = true, message = "连接限制配置已更新" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加路径连接限制
        app.MapPost("/api/connection-limit/path/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<PathConnectionLimitRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.MaxConnections <= 0)
                    return Results.Json(new { success = false, message = "无效参数" });
                accessControlService.SetPathConnectionLimit(request.Path, request.MaxConnections);
                return Results.Json(new { success = true, message = $"已设置路径 {request.Path} 最大连接数: {request.MaxConnections}" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除路径连接限制
        app.MapPost("/api/connection-limit/path/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemovePathConnectionLimitRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                    return Results.Json(new { success = false, message = "无效参数" });
                if (accessControlService.RemovePathConnectionLimit(request.Path))
                    return Results.Json(new { success = true, message = $"已移除路径 {request.Path} 连接限制" });
                return Results.Json(new { success = false, message = "路径不在限制列表中" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换 WAF Args 检测状态
        app.MapPost("/api/feature/waf-args/toggle", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !protectService.GetOptions().OpenArgsCheck;
                protectService.SetArgsCheckEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "waf-args",
                    enabled = enabled,
                    message = enabled ? "WAF Args 检测已启用" : "WAF Args 检测已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换 WAF Post 检测状态
        app.MapPost("/api/feature/waf-post/toggle", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !protectService.GetOptions().OpenPostCheck;
                protectService.SetPostCheckEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "waf-post",
                    enabled = enabled,
                    message = enabled ? "WAF Post 检测已启用" : "WAF Post 检测已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}

