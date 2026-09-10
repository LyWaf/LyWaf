using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record CreateManagementTokenRequest([property: Required, StringLength(100)] string Name,
        [property: MaxLength(100)] string[]? Scopes, DateTime? ExpiresAt);
    private sealed record CreateManagementUserRequest([property: Required, StringLength(64, MinimumLength = 3)] string Username,
        [property: Required, StringLength(1024, MinimumLength = 8)] string Password, [property: Required, StringLength(64)] string Role);
    private sealed record SetManagementUserDisabledRequest(bool Disabled);
    private sealed record SetManagementUserRoleRequest([property: Required, StringLength(64)] string Role);
    private sealed record ManagementUserResponse(string Id, string Username, string Role, bool Disabled,
        DateTime CreatedAt, DateTime? LastLoginAt, string[] Permissions);
    private sealed record ManagementUserCreatedResponse(bool Success, ManagementUserResponse Data);
    private sealed record ManagementIdentitySuccess(bool Success);
    private sealed record ManagementPasswordResponse(bool Success, string Password);
    private sealed record ManagementTokenCreatedResponse(bool Success, ManagementTokenInfo Data, string Secret);

    private static void MapManagementIdentityEndpoints(WebApplication app, int controlPort)
    {
        app.MapGet("/api/auth/users", (HttpContext context, IAuthService auth) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);
            var page = ManagementLists.From(auth.GetUsers()).Sort("username", user => user.Username)
                .Sort("createdAt", user => user.CreatedAt).Sort("lastLoginAt", user => user.LastLoginAt)
                .Sort("role", user => user.Role).Search(user => user.Username + " " + user.Role)
                .Timestamp(user => user.CreatedAt).Read(context.Request.Query, "username");
            return Results.Ok(new ManagementPage<ManagementUserResponse>(page.Success, page.Data.Select(user => ToUserResponse(user, auth)).ToArray(),
                page.Total, page.Page, page.PageSize, page.SortBy, page.SortDirection));
        }).Produces<ManagementPage<ManagementUserResponse>>()
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapPost("/api/auth/users", (HttpContext context, IAuthService auth, IAuditLogService audit, CreateManagementUserRequest request) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);

            if (request is null) return Results.BadRequest(new { success = false, message = "请求不能为空" });
            {
                var user = auth.CreateUser(request.Username, request.Password, request.Role);
                audit.Log(GetIdentityName(context), $"创建管理用户: {user.Username}", context.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new ManagementUserCreatedResponse(true, ToUserResponse(user, auth)));
            }
        }).Produces<ManagementUserCreatedResponse>()
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapPut("/api/auth/users/{id}/disabled", (string id, HttpContext context, IAuthService auth, IAuditLogService audit, SetManagementUserDisabledRequest request) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);

            if (request is null) return Results.BadRequest(new { success = false, message = "请求不能为空" });
            if (!auth.SetUserDisabled(id, request.Disabled))
                return Results.BadRequest(new { success = false, message = "用户不存在，或不能禁用最后一个管理员" });
            audit.Log(GetIdentityName(context), $"{(request.Disabled ? "禁用" : "启用")}管理用户: {id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>().RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapPut("/api/auth/users/{id}/role", (string id, HttpContext context, IAuthService auth, IAuditLogService audit, SetManagementUserRoleRequest request) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);

            if (request is null || !auth.SetUserRole(id, request.Role))
                return Results.BadRequest(new { success = false, message = "角色无效、用户不存在，或不能降权最后一个管理员" });
            audit.Log(GetIdentityName(context), $"修改管理用户角色: {id} -> {request.Role}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>().RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapPost("/api/auth/users/{id}/reset-password", (string id, HttpContext context, IAuthService auth, IAuditLogService audit) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);
            var password = auth.ResetUserPassword(id);
            if (password is null) return Results.NotFound(new { success = false, message = "用户不存在" });
            audit.Log(GetIdentityName(context), $"重置管理用户密码: {id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new ManagementPasswordResponse(true, password));
        }).Produces<ManagementPasswordResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapPost("/api/auth/users/{id}/revoke-sessions", (string id, HttpContext context, IAuthService auth, IAuditLogService audit) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);
            if (!auth.RevokeUserSessions(id)) return Results.NotFound(new { success = false, message = "用户不存在" });
            audit.Log(GetIdentityName(context), $"撤销管理用户会话: {id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>().RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapGet("/api/auth/login-history", (HttpContext context, IAuthService auth) =>
        {
            if (!HasUserManagementPermission(context)) return Results.StatusCode(403);
            return Results.Ok(ManagementLists.From(auth.GetLoginHistory(1000)).Sort("occurredAt", item => item.OccurredAt)
                .Sort("username", item => item.Username).Search(item => item.Username + " " + item.ClientIp)
                .Timestamp(item => item.OccurredAt).Read(context.Request.Query, "occurredAt", true));
        }).Produces<ManagementPage<ManagementLoginRecord>>().RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageUsers);

        app.MapGet("/api/auth/tokens", (HttpContext context, IManagementTokenService tokens) =>
        {
            if (!IsAdministrator(context)) return Results.StatusCode(403);
            return Results.Ok(ManagementLists.From(tokens.List()).Sort("createdAt", item => item.CreatedAt)
                .Sort("name", item => item.Name).Sort("lastUsedAt", item => item.LastUsedAt)
                .Search(item => item.Name + " " + string.Join(' ', item.Scopes))
                .Timestamp(item => item.CreatedAt).Read(context.Request.Query, "createdAt", true));
        }).Produces<ManagementPage<ManagementTokenInfo>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/auth/tokens", (HttpContext context, IManagementTokenService tokens, IAuditLogService audit, CreateManagementTokenRequest request) =>
        {
            if (!IsAdministrator(context)) return Results.StatusCode(403);

            if (request is null) return Results.BadRequest(new { success = false, message = "请求不能为空" });
            {
                var created = tokens.Create(request.Name, request.Scopes ?? ["read"], request.ExpiresAt);
                audit.Log(GetIdentityName(context), $"创建管理 API Token: {created.Token.Name}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new ManagementTokenCreatedResponse(true, created.Token, created.Secret));
            }
        }).Produces<ManagementTokenCreatedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapDelete("/api/auth/tokens/{id}", (string id, HttpContext context,
            IManagementTokenService tokens, IAuditLogService audit) =>
        {
            if (!IsAdministrator(context)) return Results.StatusCode(403);
            if (!tokens.Revoke(id)) return Results.NotFound(new { success = false, message = "Token 不存在或已吊销" });
            audit.Log(GetIdentityName(context), $"吊销管理 API Token: {id}",
                context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>().RequireHost($"*:{controlPort}").RequireManagementPermission();
    }

    private static bool IsAdministrator(HttpContext context) =>
        string.Equals(context.Items["ManagementRole"]?.ToString(), "admin", StringComparison.Ordinal);

    private static bool HasUserManagementPermission(HttpContext context) => IsAdministrator(context) ||
        context.Items["ManagementScopes"] is IEnumerable<string> scopes && scopes.Contains(ManagementPermissions.ManageUsers);

    private static string GetIdentityName(HttpContext context) =>
        context.Items["Username"]?.ToString() ?? "unknown";

    private static ManagementUserResponse ToUserResponse(ManagementUser user, IAuthService auth) => new(
        user.Id, user.Username, user.Role, user.Disabled, user.CreatedAt, user.LastLoginAt,
        auth.GetRoles().FirstOrDefault(role => role.Name == user.Role)?.Permissions ?? []);
}
