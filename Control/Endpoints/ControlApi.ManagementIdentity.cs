using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record CreateManagementTokenRequest(string Name, string[]? Scopes, DateTime? ExpiresAt);

    private static void MapManagementIdentityEndpoints(WebApplication app, int controlPort)
    {
        app.MapGet("/api/auth/tokens", (HttpContext context, IManagementTokenService tokens) =>
        {
            if (!IsAdministrator(context)) return Results.Forbid();
            return Results.Json(new { success = true, data = tokens.List() });
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/auth/tokens", async (HttpContext context, IManagementTokenService tokens, IAuditLogService audit) =>
        {
            if (!IsAdministrator(context)) return Results.Forbid();
            var request = await context.Request.ReadFromJsonAsync<CreateManagementTokenRequest>();
            if (request is null) return Results.BadRequest(new { success = false, message = "请求不能为空" });
            try
            {
                var created = tokens.Create(request.Name, request.Scopes ?? ["read"], request.ExpiresAt);
                audit.Log(GetIdentityName(context), $"创建管理 API Token: {created.Token.Name}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new { success = true, data = created.Token, secret = created.Secret });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        app.MapDelete("/api/auth/tokens/{id}", (string id, HttpContext context,
            IManagementTokenService tokens, IAuditLogService audit) =>
        {
            if (!IsAdministrator(context)) return Results.Forbid();
            if (!tokens.Revoke(id)) return Results.NotFound(new { success = false, message = "Token 不存在或已吊销" });
            audit.Log(GetIdentityName(context), $"吊销管理 API Token: {id}",
                context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true });
        }).RequireHost($"*:{controlPort}");
    }

    private static bool IsAdministrator(HttpContext context) =>
        string.Equals(context.Items["ManagementRole"]?.ToString(), "admin", StringComparison.Ordinal);

    private static string GetIdentityName(HttpContext context) =>
        context.Items["Username"]?.ToString() ?? "unknown";
}
