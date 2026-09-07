using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Control.Infrastructure;
using LyWaf.Services.AuditLog;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Sys.SiteAuth;

/// <summary>站点认证插件的配置查询、策略、用户和 OAuth 管理业务。</summary>
public sealed class SiteAuthAdminService(
    IOptionsMonitor<SiteAuthOptions> options,
    ControlConfigurationPatchService patches,
    IAuditLogService audit)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IResult> HandleAsync(string path, HttpContext context)
    {
        var method = context.Request.Method;
        if (method == HttpMethods.Get && path == "config") return GetConfig();
        if (method == HttpMethods.Put && path == "settings") return await UpdateSettingsAsync(context);
        if (method == HttpMethods.Post && path == "policies") return await AddPolicyAsync(context);
        if (method == HttpMethods.Put && path.StartsWith("policies/", StringComparison.Ordinal))
            return await UpdatePolicyAsync(Uri.UnescapeDataString(path["policies/".Length..]), context);
        if (method == HttpMethods.Delete && path.StartsWith("policies/", StringComparison.Ordinal))
            return await DeletePolicyAsync(Uri.UnescapeDataString(path["policies/".Length..]), context);
        if (method == HttpMethods.Post && path == "users") return await AddUserAsync(context);
        if (method == HttpMethods.Put && path.StartsWith("users/", StringComparison.Ordinal))
            return await UpdateUserAsync(Uri.UnescapeDataString(path["users/".Length..]), context);
        if (method == HttpMethods.Delete && path.StartsWith("users/", StringComparison.Ordinal))
            return await DeleteUserAsync(Uri.UnescapeDataString(path["users/".Length..]), context);
        if (method == HttpMethods.Put && path == "oauth2") return await UpdateOAuthAsync(context);
        return Results.NotFound(new { success = false, message = $"站点认证插件 API 不存在: {method} {path}" });
    }

    private IResult GetConfig()
    {
        var current = options.CurrentValue;
        return Results.Json(new
        {
            success = true,
            config = new
            {
                current.CookieName, current.SessionMinutes, current.MaxFailuresPerMinute,
                sessionSecretConfigured = !string.IsNullOrWhiteSpace(current.SessionSecret),
                current.Policies,
                users = current.Users.Select(x => new { x.Username, x.Groups, x.Enabled, passwordConfigured = !string.IsNullOrWhiteSpace(x.PasswordHash) }),
                oauth2 = new { current.OAuth2.Enabled, current.OAuth2.AuthorizationEndpoint,
                    current.OAuth2.TokenEndpoint, current.OAuth2.UserInfoEndpoint, current.OAuth2.ClientId,
                    clientSecretConfigured = !string.IsNullOrWhiteSpace(current.OAuth2.ClientSecret),
                    current.OAuth2.RedirectUri, current.OAuth2.Scope, current.OAuth2.UsernameClaim, current.OAuth2.GroupsClaim },
            },
        });
    }

    private async Task<IResult> UpdateSettingsAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<SiteAuthSettingsRequest>(context.RequestAborted);
        if (request == null) return Bad("请求内容不能为空");
        if (request.SessionMinutes is < 1 or > 10080 || request.MaxFailuresPerMinute is < 1 or > 1000) return Bad("会话时间或失败限速超出范围");
        if (string.IsNullOrWhiteSpace(request.CookieName) || request.CookieName.Length > 128) return Bad("Cookie 名称无效");
        if (request.SessionSecret is { Length: > 0 and < 32 }) return Bad("新会话密钥至少需要 32 个字符");
        return await MutateAsync(context, "更新站点认证设置", current =>
        {
            current.CookieName = request.CookieName;
            current.SessionMinutes = request.SessionMinutes;
            current.MaxFailuresPerMinute = request.MaxFailuresPerMinute;
            if (!string.IsNullOrWhiteSpace(request.SessionSecret)) current.SessionSecret = request.SessionSecret;
            return null;
        });
    }

    private async Task<IResult> AddPolicyAsync(HttpContext context)
    {
        var policy = await context.Request.ReadFromJsonAsync<SiteAuthPolicy>(context.RequestAborted);
        var error = ValidatePolicy(policy);
        if (error != null) return Bad(error);
        policy!.Id = string.IsNullOrWhiteSpace(policy.Id) ? Guid.NewGuid().ToString("N")[..12] : policy.Id;
        return await MutateAsync(context, $"添加站点认证策略: {policy.Host}{policy.Path}", current =>
        {
            if (current.Policies.Any(x => x.Id.Equals(policy.Id, StringComparison.OrdinalIgnoreCase))) return "策略 ID 已存在";
            current.Policies.Add(policy); return null;
        });
    }

    private async Task<IResult> UpdatePolicyAsync(string id, HttpContext context)
    {
        var policy = await context.Request.ReadFromJsonAsync<SiteAuthPolicy>(context.RequestAborted);
        var error = ValidatePolicy(policy);
        if (error != null) return Bad(error);
        policy!.Id = id;
        return await MutateAsync(context, $"更新站点认证策略: {id}", current =>
        {
            var index = current.Policies.FindIndex(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return "策略不存在";
            current.Policies[index] = policy; return null;
        });
    }

    private Task<IResult> DeletePolicyAsync(string id, HttpContext context) =>
        MutateAsync(context, $"删除站点认证策略: {id}", current =>
            current.Policies.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0 ? "策略不存在" : null);

    private async Task<IResult> AddUserAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<SiteAuthUserRequest>(context.RequestAborted);
        var error = ValidateUser(request, passwordRequired: true);
        if (error != null) return Bad(error);
        return await MutateAsync(context, $"添加站点认证用户: {request!.Username}", current =>
        {
            if (current.Users.Any(x => x.Username.Equals(request.Username, StringComparison.Ordinal))) return "用户名已存在";
            current.Users.Add(new SiteAuthUser { Username = request.Username, PasswordHash = HashPassword(request.Password!),
                Groups = request.Groups, Enabled = request.Enabled }); return null;
        });
    }

    private async Task<IResult> UpdateUserAsync(string username, HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<SiteAuthUserRequest>(context.RequestAborted);
        var error = ValidateUser(request, passwordRequired: false);
        if (error != null) return Bad(error);
        return await MutateAsync(context, $"更新站点认证用户: {username}", current =>
        {
            var user = current.Users.FirstOrDefault(x => x.Username.Equals(username, StringComparison.Ordinal));
            if (user == null) return "用户不存在";
            user.Groups = request!.Groups; user.Enabled = request.Enabled;
            if (!string.IsNullOrEmpty(request.Password)) user.PasswordHash = HashPassword(request.Password);
            return null;
        });
    }

    private Task<IResult> DeleteUserAsync(string username, HttpContext context) =>
        MutateAsync(context, $"删除站点认证用户: {username}", current =>
            current.Users.RemoveAll(x => x.Username.Equals(username, StringComparison.Ordinal)) == 0 ? "用户不存在" : null);

    private async Task<IResult> UpdateOAuthAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync<OAuth2UpdateRequest>(context.RequestAborted);
        if (request == null) return Bad("请求内容不能为空");
        if (request.Enabled && (!Https(request.AuthorizationEndpoint) || !Https(request.TokenEndpoint) || !Https(request.UserInfoEndpoint) || !Https(request.RedirectUri)))
            return Bad("启用 OAuth2 时所有端点和回调必须使用 HTTPS");
        return await MutateAsync(context, "更新站点 OAuth2 配置", current =>
        {
            var secret = string.IsNullOrWhiteSpace(request.ClientSecret) ? current.OAuth2.ClientSecret : request.ClientSecret;
            current.OAuth2 = new OAuth2Options { Enabled = request.Enabled, AuthorizationEndpoint = request.AuthorizationEndpoint,
                TokenEndpoint = request.TokenEndpoint, UserInfoEndpoint = request.UserInfoEndpoint, ClientId = request.ClientId,
                ClientSecret = secret, RedirectUri = request.RedirectUri, Scope = request.Scope,
                UsernameClaim = request.UsernameClaim, GroupsClaim = request.GroupsClaim };
            return null;
        });
    }

    private async Task<IResult> MutateAsync(HttpContext context, string action, Func<SiteAuthOptions, string?> mutation)
    {
        await _gate.WaitAsync(context.RequestAborted);
        try
        {
            var next = Clone(options.CurrentValue);
            var error = mutation(next);
            if (error != null) return Results.Json(new { success = false, message = error }, statusCode: 404);
            var impact = await patches.SetPathAsync(["Plugins", "site-auth"], next, "site-auth-update", context.RequestAborted);
            audit.Log(context.Items["Username"]?.ToString() ?? "unknown", action, context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true, impact });
        }
        finally { _gate.Release(); }
    }

    private static SiteAuthOptions Clone(SiteAuthOptions value) => JsonSerializer.Deserialize<SiteAuthOptions>(JsonSerializer.Serialize(value))!;
    private static string? ValidatePolicy(SiteAuthPolicy? policy)
    {
        if (policy == null || string.IsNullOrWhiteSpace(policy.Host) || policy.Host.Contains("://")) return "Host 只能填写域名";
        if (!policy.Path.StartsWith('/') || policy.Path.StartsWith("//")) return "Path 必须是站内绝对路径";
        if (policy.Mode is not ("Form" or "Basic" or "OAuth2")) return "Mode 必须是 Form、Basic 或 OAuth2";
        return null;
    }
    private static string? ValidateUser(SiteAuthUserRequest? request, bool passwordRequired)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 128) return "用户名无效";
        if ((passwordRequired && string.IsNullOrEmpty(request.Password)) || request.Password is { Length: > 0 and < 12 }) return "密码至少需要 12 个字符";
        return request.Groups.Count > 64 || request.Groups.Any(x => x.Length > 128) ? "用户组数量或长度超限" : null;
    }
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    private static bool Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    private static IResult Bad(string message) => Results.Json(new { success = false, message }, statusCode: 400);
}

public sealed class SiteAuthSettingsRequest
{
    public string CookieName { get; set; } = "__Host-LyWafSiteAuth";
    public int SessionMinutes { get; set; } = 480;
    public int MaxFailuresPerMinute { get; set; } = 10;
    public string? SessionSecret { get; set; }
}
public sealed class SiteAuthUserRequest
{
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    public List<string> Groups { get; set; } = [];
    public bool Enabled { get; set; } = true;
}
public sealed class OAuth2UpdateRequest : OAuth2Options
{
    public new string? ClientSecret { get; set; }
}
