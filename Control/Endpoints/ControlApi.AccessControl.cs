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
    private sealed record IpGroupResponse(bool Success, IpGroup Group, string Message);
    private sealed record IpGroupDeletedResponse(bool Success, string Message);
    private sealed record IpAccessResponse(bool Success, string Message, string IpOrCidr, DateTime Timestamp);
    private static void MapAccessControlEndpoints(WebApplication app, int controlPort)
    {
        MapIpAccessListEndpoints(app, controlPort);
        // =============== 动态访问控制管理 API ===============

        app.MapGet("/api/ip-groups", (HttpContext ctx, IIpGroupService ipGroupService) =>
        {
            var page = ManagementLists.From(ipGroupService.GetAll())
                .Sort("name", group => group.Name).Sort("createdAt", group => group.CreatedAt)
                .Sort("updatedAt", group => group.UpdatedAt).Sort("entryCount", group => group.Entries.Count)
                .Search(group => group.Name + " " + group.Description + " " + string.Join(' ', group.Entries))
                .Timestamp(group => group.UpdatedAt).Read(ctx.Request.Query, "updatedAt", true);
            return Results.Ok(page);
        })
            .Produces<ManagementPage<IpGroup>>()
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/ip-groups", (HttpContext ctx, IIpGroupService ipGroupService, SaveIpGroupRequest request) =>
        {

            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            var group = ipGroupService.Create(request.Name, request.Description, request.Entries);
            ctx.RequestServices.GetRequiredService<IAuditLogService>().Log(
                ctx.Items["Username"]?.ToString() ?? "unknown", $"创建 IP 组: {group.Name}",
                ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new IpGroupResponse(true, group, "IP 组已创建"));
        }).Produces<IpGroupResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/config/history", async (HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<ConfigVersionService>();
            return Results.Ok(ManagementLists.From(await service.GetVersionsAsync(ctx.RequestAborted))
                .Sort("createdAt", version => version.CreatedAt).Sort("kind", version => version.Kind)
                .Sort("operator", version => version.Operator).Sort("action", version => version.Action)
                .Search(version => $"{version.Id} {version.Kind} {version.Operator} {version.Action}")
                .Timestamp(version => version.CreatedAt).Read(ctx.Request.Query, "createdAt", true));
        }).Produces<ManagementPage<ConfigVersionInfo>>()
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/config/history/{id}/diff", async (string id, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<ConfigVersionService>();
            var diff = await service.GetDiffAsync(id, ctx.RequestAborted);
            return diff == null
                ? Results.Json(new { success = false, message = "配置版本不存在" }, statusCode: 404)
                : Results.Json(new { success = true, diff });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/config/history/{id}/rollback", (string id, HttpContext ctx, IConfiguration config) =>
            QueueConfigurationChange(ctx, config, null, id))
            .Produces<LyWaf.Services.ManagementTasks.ManagementJobResponse>(202)
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPut("/api/ip-groups/{id}", (string id, HttpContext ctx, IIpGroupService ipGroupService, SaveIpGroupRequest request) =>
        {

            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            var group = ipGroupService.Update(id, request.Name, request.Description, request.Entries);
            if (group == null) return Results.NotFound(new { success = false, message = "IP 组不存在" });
            ctx.RequestServices.GetRequiredService<IAuditLogService>().Log(
                ctx.Items["Username"]?.ToString() ?? "unknown", $"更新 IP 组: {group.Name}",
                ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new IpGroupResponse(true, group, "IP 组已更新"));
        }).Produces<IpGroupResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapDelete("/api/ip-groups/{id}", (string id, HttpContext ctx, IIpGroupService ipGroupService) =>
        {
            if (!ipGroupService.Delete(id))
                return Results.NotFound(new { success = false, message = "IP 组不存在" });
            ctx.RequestServices.GetRequiredService<IAuditLogService>().Log(
                ctx.Items["Username"]?.ToString() ?? "unknown", $"删除 IP 组: {id}",
                ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new IpGroupDeletedResponse(true, "IP 组已删除"));
        }).Produces<IpGroupDeletedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPut("/api/ip-groups/{id}/usage", (string id, HttpContext ctx, IIpGroupService ipGroupService, SetIpGroupUsageRequest request) =>
        {

            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            var group = ipGroupService.SetUsage(id, request.WhitelistEnabled, request.BlacklistEnabled);
            if (group == null) return Results.NotFound(new { success = false, message = "IP 组不存在" });
            ctx.RequestServices.GetRequiredService<IAuditLogService>().Log(
                ctx.Items["Username"]?.ToString() ?? "unknown",
                $"更新 IP 组名单绑定: {group.Name}, 白名单={group.WhitelistEnabled}, 黑名单={group.BlacklistEnabled}",
                ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new IpGroupResponse(true, group, "IP 组名单绑定已更新"));
        }).Produces<IpGroupResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取所有访问控制数据（黑白名单页面用）
        app.MapGet("/api/ac/all", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var whitelist = accessControlService.GetWhitelist();
            var blacklist = accessControlService.GetBlacklist();
            var denyCountries = accessControlService.GetDenyCountries();
            var denyRegions = accessControlService.GetDenyRegions();
            var allowCountries = accessControlService.GetAllowCountries();
            var allowRegions = accessControlService.GetAllowRegions();
            var secSnapshot = SharedData.Security.GetSnapshot();

            return TypedResults.Ok(new
            {
                success = true,
                ipControl = new
                {
                    enabled = options.IpControl.Enabled,
                    whitelist,
                    blacklist,
                },
                geoControl = new
                {
                    enabled = options.GeoControl.Enabled,
                    mode = options.GeoControl.Mode.ToString(),
                    denyCountries,
                    denyRegions,
                    allowCountries,
                    allowRegions,
                },
                stats = new
                {
                    blacklistBlockCount = secSnapshot.BlacklistBlockCount,
                    geoBlockCount = secSnapshot.GeoBlockCount,
                },
                rejectStatusCode = options.RejectStatusCode,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 切换 IP 访问控制启用状态
        app.MapPost("/api/ac/ip-control/toggle", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var newState = !options.IpControl.Enabled;
            accessControlService.SetIpControlEnabled(newState);
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"IP 访问控制: {(newState ? "启用" : "禁用")}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true, enabled = newState, message = newState ? "IP 访问控制已启用" : "IP 访问控制已禁用" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 切换地理位置访问控制启用状态
        app.MapPost("/api/ac/geo-control/toggle", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var newState = !options.GeoControl.Enabled;
            accessControlService.SetGeoControlEnabled(newState);
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"地理位置访问控制: {(newState ? "启用" : "禁用")}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true, enabled = newState, message = newState ? "地理位置访问控制已启用" : "地理位置访问控制已禁用" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取白名单列表
        app.MapGet("/api/ac/whitelist", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var whitelist = accessControlService.GetWhitelist();
            return TypedResults.Ok(ManagementLists.From(whitelist).Sort("value", value => value).Search(value => value).Read(ctx.Request.Query, "value"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加 IP 到白名单
        app.MapPost("/api/ac/whitelist/add", (HttpContext ctx, IAccessControlService accessControlService, AddIpRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
            {
                return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
            }

            var result = accessControlService.AddWhitelist(request.IpOrCidr);
            if (result)
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"添加白名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new
                {
                    success = true,
                    message = $"已成功添加白名单: {request.IpOrCidr}",
                    ipOrCidr = request.IpOrCidr,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "添加失败：IP 格式无效或已存在" }, statusCode: 400);
            }
        }).Produces<IpAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 从白名单移除 IP
        app.MapPost("/api/ac/whitelist/remove", (HttpContext ctx, IAccessControlService accessControlService, RemoveIpRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
            {
                return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
            }

            var result = accessControlService.RemoveWhitelist(request.IpOrCidr);
            if (result)
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"移除白名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new
                {
                    success = true,
                    message = $"已成功从白名单移除: {request.IpOrCidr}",
                    ipOrCidr = request.IpOrCidr,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "移除失败：IP 不在动态白名单中" }, statusCode: 404);
            }
        }).Produces<IpAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取黑名单列表
        app.MapGet("/api/ac/blacklist", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var blacklist = accessControlService.GetBlacklist();
            return TypedResults.Ok(ManagementLists.From(blacklist).Sort("value", value => value).Search(value => value).Read(ctx.Request.Query, "value"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加 IP 到黑名单
        app.MapPost("/api/ac/blacklist/add", (HttpContext ctx, IAccessControlService accessControlService, AddIpRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
            {
                return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
            }

            var result = accessControlService.AddBlacklist(request.IpOrCidr);
            if (result)
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"添加黑名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new
                {
                    success = true,
                    message = $"已成功添加黑名单: {request.IpOrCidr}",
                    ipOrCidr = request.IpOrCidr,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "添加失败：IP 格式无效或已存在" }, statusCode: 400);
            }
        }).Produces<IpAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 从黑名单移除 IP
        app.MapPost("/api/ac/blacklist/remove", (HttpContext ctx, IAccessControlService accessControlService, RemoveIpRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
            {
                return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
            }

            var result = accessControlService.RemoveBlacklist(request.IpOrCidr);
            if (result)
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"移除黑名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new
                {
                    success = true,
                    message = $"已成功从黑名单移除: {request.IpOrCidr}",
                    ipOrCidr = request.IpOrCidr,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "移除失败：IP 不在动态黑名单中" }, statusCode: 404);
            }
        }).Produces<IpAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
