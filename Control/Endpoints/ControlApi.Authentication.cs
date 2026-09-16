using System.Text;
using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using Microsoft.Extensions.FileProviders;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record AuthenticationResponse(bool Success, string? Token, string? Username, string? Role,
        IReadOnlyCollection<string> Permissions, DateTime? ExpiresAt);
    private sealed record TokenRefreshResponse(bool Success, string? Token, DateTime? ExpiresAt);
    private sealed record PasswordChangedResponse(bool Success, string Message);
    private static void MapAuthenticationEndpoints(WebApplication app, int controlPort)
    {
        // =============== 认证端点（免认证） ===============

        app.MapPost("/api/auth/login", (HttpContext ctx, LoginRequest request) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();

            if (request == null || string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.PasswordHash))
            {
                return Results.Json(new { success = false, message = "用户名和密码不能为空" }, statusCode: 400);
            }
            var result = authService.Login(request.Username, request.PasswordHash, request.Timestamp, request.SecondFactor);
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            authService.RecordLoginAttempt(request.Username, result.Success, clientIp);
            if (!result.Success)
            {
                var auditFail = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                auditFail.Log(request.Username, "登录失败", clientIp);
                return Results.Json(new
                {
                    success = false,
                    message = result.Message ?? "用户名或密码错误",
                    retryAfterSeconds = result.RetryAfterSeconds
                }, statusCode: 401);
            }
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(result.Username ?? request.Username, "登录成功", clientIp);
            return Results.Json(new AuthenticationResponse(true, result.Token, result.Username, result.Role, result.Permissions, result.ExpiresAt));
        }).Produces<AuthenticationResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/auth/time", (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            return TypedResults.Ok(new { timestamp = authService.GetServerTimestamp() });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/auth/check", () =>
        {
            return TypedResults.Ok(new { success = true, authRequired = true });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== JWT 认证中间件 ===============

        app.Use(async (context, next) =>
        {
            // 仅对控制端口生效
            if (context.Connection.LocalPort != controlPort)
            {
                await next();
                return;
            }

            var path = context.Request.Path.Value ?? "";

            // 跳过非 API 路径（静态文件、SPA 页面）
            if (!path.StartsWith("/api/"))
            {
                await next();
                return;
            }

            // 跳过免认证端点
            if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/time", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/check", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/health/ready", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/distribution/agent/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/runtime-state/agent/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/pcap/ca.crl", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // 验证身份：支持 Bearer Token 和 Basic Auth（curl 快速鉴权）
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader))
            {
                context.Response.StatusCode = 401;
                // Advertise Bearer only so browsers leave sign-in to the SPA instead of opening a Basic dialog.
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(context, "unauthorized", "未授权访问"));
                return;
            }

            var authService = context.RequestServices.GetRequiredService<IAuthService>();

            // Bearer Token 鉴权
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader["Bearer ".Length..];
                if (token.StartsWith("lywaf_pat_", StringComparison.Ordinal))
                {
                    var apiTokens = context.RequestServices.GetRequiredService<IManagementTokenService>();
                    var tokenIdentity = apiTokens.Authenticate(token);
                    if (tokenIdentity is null)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(
                            context, "invalid_api_token", "API Token 无效、已过期或已吊销"));
                        return;
                    }
                    if (!ManagementAuthorization.IsAllowed(context, tokenIdentity.Scopes))
                    {
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(
                            context, "insufficient_scope", "当前 API Token 只有只读权限"));
                        return;
                    }
                    context.Items["Username"] = $"token:{tokenIdentity.Name}";
                    context.Items["ManagementRole"] = tokenIdentity.Scopes.Contains("admin") ? "admin" : "automation";
                    context.Items["ManagementScopes"] = tokenIdentity.Scopes;
                    await next();
                    return;
                }
                var identity = authService.GetIdentity(token);
                if (identity == null)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(context, "invalid_token", "令牌无效或已过期"));
                    return;
                }
                context.Items["Username"] = identity.Username;
                context.Items["ManagementRole"] = identity.Role;
                context.Items["ManagementScopes"] = identity.Permissions;
                if (!ManagementAuthorization.IsAllowed(context, identity.Permissions))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(
                        context, "permission_denied", "当前角色没有写入权限"));
                    return;
                }
                await next();
                return;
            }

            // Basic Auth 鉴权（curl -u user:pass）
            if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var encoded = authHeader["Basic ".Length..];
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    var colonIndex = decoded.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        var basicUser = decoded[..colonIndex];
                        var basicPass = decoded[(colonIndex + 1)..];
                        var result = authService.ValidateCredentials(basicUser, basicPass);
                        authService.RecordLoginAttempt(basicUser, result.Success,
                            context.Connection.RemoteIpAddress?.ToString() ?? "");
                        if (result.Success)
                        {
                            context.Items["Username"] = basicUser;
                            context.Items["ManagementRole"] = result.Role;
                            context.Items["ManagementScopes"] = result.Permissions;
                            if (!ManagementAuthorization.IsAllowed(context, result.Permissions))
                            {
                                context.Response.StatusCode = 403;
                                await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(
                                    context, "permission_denied", "当前角色没有写入权限"));
                                return;
                            }
                            await next();
                            return;
                        }
                        // 验证失败，返回具体错误
                        context.Response.StatusCode = 401;
                        var message = result.Message ?? "用户名或密码错误";
                        await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(context,
                            "invalid_credentials", message, new { result.RetryAfterSeconds }));
                        return;
                    }
                }
                catch { /* Base64 解码失败，走到下面的 401 */ }
            }

            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await context.Response.WriteAsJsonAsync(ManagementApiContract.CreateError(context, "invalid_auth_scheme", "无效的认证方式"));
        });

        // =============== 认证端点（需认证） ===============

        app.MapPost("/api/auth/refresh", (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true)
                return Results.Json(new { success = false, message = "刷新令牌需要 Bearer 会话" }, statusCode: 401);
            var token = authHeader["Bearer ".Length..];
            var result = authService.RefreshToken(token);
            if (!result.Success)
            {
                return Results.Json(new { success = false, message = "刷新令牌失败" }, statusCode: 401);
            }
            return Results.Json(new TokenRefreshResponse(true, result.Token, result.ExpiresAt));
        }).Produces<TokenRefreshResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var username = GetIdentityName(ctx);
            return TypedResults.Ok(new { success = true, username });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/auth/change-password", (HttpContext ctx, ChangePasswordRequest request) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();

            if (request == null || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
            {
                return Results.Json(new { success = false, message = "密码不能为空" }, statusCode: 400);
            }
            if (request.NewPassword.Length < 6)
            {
                return Results.Json(new { success = false, message = "新密码长度至少6位" }, statusCode: 400);
            }
            var ok = authService.ChangePassword(ctx.Items["Username"]?.ToString() ?? "", request.CurrentPassword, request.NewPassword);
            if (!ok)
            {
                return Results.Json(new { success = false, message = "当前密码错误" }, statusCode: 400);
            }
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "修改密码", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new PasswordChangedResponse(true, "密码修改成功"));
        }).Produces<PasswordChangedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

    }

    private static bool IsWriteRequest(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
}
