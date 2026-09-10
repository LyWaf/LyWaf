using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record SaveManagementRoleRequest([property: Required, StringLength(64)] string Name,
        [property: Required, MaxLength(100)] string[] Permissions);

    private static void MapManagementRoleEndpoints(WebApplication app, int port)
    {
        var group = app.MapGroup("/api/auth/roles").RequireHost($"*:{port}")
            .RequireManagementPermission(ManagementPermissions.ManageUsers);
        group.MapGet("", (HttpContext context, IAuthService auth) => Results.Ok(ManagementLists.From(auth.GetRoles())
            .Sort("name", role => role.Name).Sort("builtIn", role => role.BuiltIn)
            .Search(role => role.Name + " " + string.Join(' ', role.Permissions)).Read(context.Request.Query, "name")))
            .Produces<ManagementPage<ManagementRoleDefinition>>();
        group.MapPost("", (SaveManagementRoleRequest request, HttpContext context, IAuthService auth, IAuditLogService audit) =>
        {
            { auth.SaveRole(request.Name, request.Permissions); }
            audit.Log(GetIdentityName(context), $"保存管理角色: {request.Name}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true });
        });
        group.MapDelete("/{name}", (string name, HttpContext context, IAuthService auth, IAuditLogService audit) =>
        {
            if (!auth.DeleteRole(name)) return Results.BadRequest(new { success = false, message = "角色不存在、为内置角色或仍被用户使用" });
            audit.Log(GetIdentityName(context), $"删除管理角色: {name}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>();
    }
}
