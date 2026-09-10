namespace LyWaf.Services.Auth;

/// <summary>显式免除二次验证，正常身份认证、权限校验与审计仍然生效。</summary>
public sealed record ManagementStepUpExemption;

/// <summary>人员敏感写操作要求短期二次验证；自动化由独立 API Token 权限控制。</summary>
public static class ManagementStepUpMiddleware
{
    public static void UseManagementStepUp(this WebApplication app, int port)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort != port || HttpMethods.IsGet(ctx.Request.Method) ||
                HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method))
            { await next(); return; }
            var path = ctx.Request.Path;
            if (ctx.GetEndpoint()?.Metadata.GetMetadata<ManagementStepUpExemption>() != null)
            { await next(); return; }
            var requirements = ctx.GetEndpoint()?.Metadata.GetOrderedMetadata<ManagementPermissionRequirement>();
            var sensitive = requirements?.Any(r => r.Permission is ManagementPermissions.ManageUsers or ManagementPermissions.ManageSystem) == true ||
                path.StartsWithSegments("/api/auth/tokens") || path.StartsWithSegments("/api/config") ||
                path.StartsWithSegments("/api/plugins") || path.StartsWithSegments("/api/settings");
            if (!sensitive) { await next(); return; }
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer lywaf_pat_", StringComparison.Ordinal)) { await next(); return; }
            var auth = ctx.RequestServices.GetRequiredService<IAuthService>();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !auth.ValidateStepUp(header[7..], ctx.Request.Headers["X-LyWaf-Step-Up"].FirstOrDefault()))
            {
                ctx.Response.StatusCode = 428;
                await ctx.Response.WriteAsJsonAsync(new { success = false, code = "step_up_required", message = "敏感操作需要重新验证身份" });
                return;
            }
            await next();
        });
    }
}
