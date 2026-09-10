using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record MfaRequest([property: Required, StringLength(1024)] string Password, [property: StringLength(64)] string? Code);
    private sealed record MfaProofResponse(bool Success, string Proof);
    private sealed record MfaEnrollmentResponse(bool Success, TotpEnrollment Enrollment);
    private sealed record MfaRecoveryResponse(bool Success, string[] RecoveryCodes);

    private static void MapManagementMfaEndpoints(WebApplication app, int controlPort)
    {
        var group = app.MapGroup("/api/auth/mfa").RequireHost($"*:{controlPort}").RequireManagementPermission()
            .RequireManagementPermission(ManagementPermissions.Read);
        group.AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            var header = ctx.Request.Headers.Authorization.ToString();
            var auth = ctx.RequestServices.GetRequiredService<IAuthService>();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || auth.GetIdentity(header[7..]) is null)
                return Results.StatusCode(403);
            ctx.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", (HttpContext ctx, IAuthService auth) =>
            TypedResults.Ok(new { success = true, enabled = auth.IsTotpEnabled(ctx.Items["Username"]!.ToString()!) }));
        group.MapPost("/{action}", (string action, MfaRequest request, HttpContext ctx, IAuthService auth, IAuditLogService audit) =>
        {
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024 || request.Code?.Length > 64)
                return Results.BadRequest(new { success = false, message = "请输入有效密码及动态码" });
            var username = ctx.Items["Username"]!.ToString()!;
            {
                IResult result;
                switch (action)
                {
                    case "step-up": result = Results.Ok(new MfaProofResponse(true, auth.CreateStepUp(username, request.Password, request.Code, ctx.Request.Headers.Authorization.ToString()[7..]))); break;
                    case "setup": result = Results.Ok(new MfaEnrollmentResponse(true, auth.BeginTotp(username, request.Password))); break;
                    case "confirm": result = Results.Ok(new MfaRecoveryResponse(true, auth.ConfirmTotp(username, request.Password, request.Code ?? ""))); break;
                    case "disable":
                        if (!auth.DisableTotp(username, request.Password, request.Code ?? ""))
                            return Results.BadRequest(new { success = false, message = "动态码或恢复码无效" });
                        result = Results.Ok(new ManagementIdentitySuccess(true)); break;
                    default: return Results.NotFound();
                }
                audit.Log(username, $"双因素认证 {action}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return result;
            }
        }).Produces<MfaProofResponse>().Produces<MfaEnrollmentResponse>().Produces<MfaRecoveryResponse>().Produces<ManagementIdentitySuccess>();
    }
}
