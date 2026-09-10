namespace LyWaf.Services.Auth;

/// <summary>控制台权限名称。端点和角色均复用这些稳定标识。</summary>
public static class ManagementPermissions
{
    public const string Read = "read";
    public const string Write = "write";
    public const string ManageUsers = "users.manage";
    public const string ManageSystem = "system.manage";

    public static IReadOnlySet<string> ForRole(string role) => role.ToLowerInvariant() switch
    {
        ManagementRoles.Admin => new HashSet<string>([Read, Write, ManageUsers, ManageSystem], StringComparer.Ordinal),
        ManagementRoles.Auditor => new HashSet<string>([Read], StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal)
    };
}

/// <summary>内置管理角色。</summary>
public static class ManagementRoles
{
    public const string Admin = "admin";
    public const string Auditor = "auditor";
}

/// <summary>端点声明的权限；同一端点的全部权限均须满足。</summary>
public sealed record ManagementPermissionRequirement(string Permission);

public static class ManagementAuthorization
{
    public static T RequireManagementPermission<T>(this T builder) where T : IEndpointConventionBuilder
    {
        AddErrorContract(builder);
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<ManagementPermissionRequirement>().Any()) return;
            if (endpoint is not Microsoft.AspNetCore.Routing.RouteEndpointBuilder route) return;
            var path = route.RoutePattern.RawText ?? "";
            var methods = endpoint.Metadata.OfType<Microsoft.AspNetCore.Routing.HttpMethodMetadata>().FirstOrDefault()?.HttpMethods;
            var read = methods is { Count: > 0 } && methods.All(m => m is "GET" or "HEAD" or "OPTIONS");
            var permission = SensitivePrefixes.Any(p => new PathString(path).StartsWithSegments(p))
                ? ManagementPermissions.ManageSystem : read ? ManagementPermissions.Read : ManagementPermissions.Write;
            endpoint.Metadata.Add(new ManagementPermissionRequirement(permission));
        });
        return builder;
    }

    private static readonly string[] SensitivePrefixes = ["/api/config", "/api/config-file", "/api/settings", "/api/patch", "/api/history", "/api/auth/tokens", "/api/stop", "/api/reload", "/api/restart", "/api/plugins", "/api/upgrade", "/api/lifecycle"];

    public static T RequireManagementPermission<T>(this T builder, string permission)
        where T : IEndpointConventionBuilder
    {
        AddErrorContract(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new ManagementPermissionRequirement(permission)));
        return builder;
    }

    private static void AddErrorContract(IEndpointConventionBuilder builder)
    {
        if (builder is RouteHandlerBuilder route) route.AddEndpointFilter<LyWaf.Services.ManagementApi.ManagementErrorFilter>();
        else if (builder is Microsoft.AspNetCore.Routing.RouteGroupBuilder group) group.AddEndpointFilter<LyWaf.Services.ManagementApi.ManagementErrorFilter>();
    }

    public static bool IsAllowed(HttpContext context, IEnumerable<string> permissions)
    {
        var grants = permissions.ToHashSet(StringComparer.Ordinal);
        if (grants.Contains("admin")) return true;
        var path = context.Request.Path;
        // 人员自己的会话及密码操作不要求业务写权限。
        if (path == "/api/auth/refresh" || path == "/api/auth/change-password")
            return context.Items["ManagementRole"] is string role && role != "automation";
        var requirements = context.GetEndpoint()?.Metadata.GetOrderedMetadata<ManagementPermissionRequirement>();
        if (requirements is { Count: > 0 }) return requirements.All(r => grants.Contains(r.Permission));
        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method);
        return grants.Contains(read ? ManagementPermissions.Read : ManagementPermissions.Write);
    }
}
