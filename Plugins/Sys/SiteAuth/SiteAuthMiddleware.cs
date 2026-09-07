using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LyWaf.Services.AuditLog;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Sys.SiteAuth;

public sealed class SiteAuthOptions
{
    public string SessionSecret { get; set; } = "";
    public string CookieName { get; set; } = "__Host-LyWafSiteAuth";
    public int SessionMinutes { get; set; } = 480;
    public int MaxFailuresPerMinute { get; set; } = 10;
    public List<SiteAuthPolicy> Policies { get; set; } = [];
    public List<SiteAuthUser> Users { get; set; } = [];
    public OAuth2Options OAuth2 { get; set; } = new();
}

public sealed class SiteAuthPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Host { get; set; } = "";
    public string Path { get; set; } = "/**";
    public string Mode { get; set; } = "Form";
    public List<string> AllowedGroups { get; set; } = [];
    public List<string> IpExceptions { get; set; } = [];
}

public sealed class SiteAuthUser
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public List<string> Groups { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public class OAuth2Options
{
    public bool Enabled { get; set; }
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string UserInfoEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scope { get; set; } = "openid profile email";
    public string UsernameClaim { get; set; } = "sub";
    public string GroupsClaim { get; set; } = "groups";
}

public sealed record SiteIdentity(string Username, string[] Groups, long ExpiresAt);
public sealed record OAuthExchangeResult(SiteIdentity Identity, string ReturnUrl);

/// <summary>验证本地凭据和独立站点会话；不依赖控制台管理员认证服务。</summary>
public sealed class SiteAuthService(IOptionsMonitor<SiteAuthOptions> options, IHttpClientFactory clients, IAuditLogService audit)
{
    private readonly string _ephemeralSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly ConcurrentDictionary<string, FailureWindow> _failures = new();

    public SiteAuthPolicy? Match(HttpContext context) => Match(context.Request.Host.Host, context.Request.Path);
    public SiteAuthPolicy? Match(string host, string path) => options.CurrentValue.Policies.FirstOrDefault(p =>
        HostMatches(p.Host, host) && PathMatches(p.Path, path));

    public bool IsIpExcepted(SiteAuthPolicy policy, string ip) => policy.IpExceptions.Contains(ip, StringComparer.OrdinalIgnoreCase);

    public SiteIdentity? Authenticate(string username, string password, SiteAuthPolicy policy, string ip)
    {
        if (IsLimited(ip)) return null;
        var current = options.CurrentValue;
        var user = current.Users.FirstOrDefault(x => x.Enabled && x.Username.Equals(username, StringComparison.Ordinal));
        if (user == null || !VerifyPassword(password, user.PasswordHash) || !GroupsAllowed(policy, user.Groups))
        {
            RegisterFailure(ip); audit.Log(username, "站点认证失败", ip); return null;
        }
        _failures.TryRemove(ip, out _);
        audit.Log(username, "站点认证成功", ip);
        return new(username, user.Groups.ToArray(), DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(current.SessionMinutes, 1, 10080)).ToUnixTimeSeconds());
    }

    public string Protect(SiteIdentity identity)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(identity));
        return payload + "." + Convert.ToBase64String(HMACSHA256.HashData(SessionKey(), Encoding.UTF8.GetBytes(payload)));
    }

    public SiteIdentity? Unprotect(string? token, SiteAuthPolicy policy)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var split = token.LastIndexOf('.');
        if (split < 1) return null;
        try
        {
            var payload = token[..split];
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(token[(split + 1)..]),
                    HMACSHA256.HashData(SessionKey(), Encoding.UTF8.GetBytes(payload)))) return null;
            var identity = JsonSerializer.Deserialize<SiteIdentity>(Convert.FromBase64String(payload));
            return identity != null && identity.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                   GroupsAllowed(policy, identity.Groups) ? identity : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    public string OAuthState(string returnUrl, long expiresAt) => Protect(new SiteIdentity(returnUrl, [], expiresAt));
    public string? ReadOAuthReturn(string state) => Unprotect(state, new SiteAuthPolicy())?.Username;

    public async Task<OAuthExchangeResult?> ExchangeOAuthAsync(string code, string state, SiteAuthPolicy policy, string ip,
        CancellationToken cancellationToken)
    {
        var stateValue = Unprotect(state, new SiteAuthPolicy());
        var current = options.CurrentValue;
        var oauth = current.OAuth2;
        if (stateValue == null || !oauth.Enabled || !Https(oauth.TokenEndpoint) || !Https(oauth.UserInfoEndpoint)) return null;
        using var tokenResponse = await clients.CreateClient(nameof(SiteAuthService)).PostAsync(oauth.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code,
                ["client_id"] = oauth.ClientId, ["client_secret"] = oauth.ClientSecret, ["redirect_uri"] = oauth.RedirectUri }), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode) return null;
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken));
        if (!tokenJson.RootElement.TryGetProperty("access_token", out var access)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, oauth.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
        using var userResponse = await clients.CreateClient(nameof(SiteAuthService)).SendAsync(request, cancellationToken);
        if (!userResponse.IsSuccessStatusCode) return null;
        using var userJson = JsonDocument.Parse(await userResponse.Content.ReadAsStreamAsync(cancellationToken));
        if (!userJson.RootElement.TryGetProperty(oauth.UsernameClaim, out var name)) return null;
        var groups = userJson.RootElement.TryGetProperty(oauth.GroupsClaim, out var groupValue) && groupValue.ValueKind == JsonValueKind.Array
            ? groupValue.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
        if (!GroupsAllowed(policy, groups)) { audit.Log(name.GetString() ?? "oauth-user", "站点 OAuth2 认证失败: 用户组不允许", ip); return null; }
        var identity = new SiteIdentity(name.GetString() ?? "oauth-user", groups,
            DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(current.SessionMinutes, 1, 10080)).ToUnixTimeSeconds());
        audit.Log(identity.Username, "站点 OAuth2 认证成功", ip);
        return new(identity, stateValue.Username);
    }

    public bool IsLimited(string ip) => _failures.TryGetValue(ip, out var value) && value.Since > DateTime.UtcNow.AddMinutes(-1) &&
        value.Count >= Math.Clamp(options.CurrentValue.MaxFailuresPerMinute, 1, 1000);
    private byte[] SessionKey()
    {
        var configured = options.CurrentValue.SessionSecret;
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(configured) ? _ephemeralSecret : configured));
    }
    private void RegisterFailure(string ip) => _failures.AddOrUpdate(ip, _ => new(1, DateTime.UtcNow), (_, old) =>
        old.Since <= DateTime.UtcNow.AddMinutes(-1) ? new(1, DateTime.UtcNow) : old with { Count = old.Count + 1 });
    private static bool GroupsAllowed(SiteAuthPolicy policy, IEnumerable<string> groups) => policy.AllowedGroups.Count == 0 ||
        groups.Any(x => policy.AllowedGroups.Contains(x, StringComparer.OrdinalIgnoreCase));
    private static bool Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    private static bool HostMatches(string expected, string actual) => expected == "*" || expected.Equals(actual, StringComparison.OrdinalIgnoreCase);
    private static bool PathMatches(string pattern, string path) => Regex.IsMatch(path,
        "^" + Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    private static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations is < 100000 or > 2000000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]); var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
    private sealed record FailureWindow(int Count, DateTime Since);
}

public sealed class SiteAuthMiddleware(RequestDelegate next, SiteAuthService service, IOptionsMonitor<SiteAuthOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (context.Request.Path == "/.lywaf-site-auth/login" && context.Request.Method == HttpMethods.Post)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = SafeReturn(form["returnUrl"].ToString());
            var loginPolicy = service.Match(context.Request.Host.Host, returnUrl);
            if (loginPolicy == null || !loginPolicy.Mode.Equals("Form", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 404; return; }
            var identity = service.Authenticate(form["username"].ToString(), form["password"].ToString(), loginPolicy, ip);
            if (identity == null) { context.Response.StatusCode = service.IsLimited(ip) ? 429 : 401; await WriteLogin(context, "登录失败", returnUrl); return; }
            WriteCookie(context, service.Protect(identity)); context.Response.Redirect(returnUrl); return;
        }
        if (context.Request.Path == "/.lywaf-site-auth/oauth/callback")
        {
            var state = context.Request.Query["state"].ToString();
            var returnUrl = SafeReturn(service.ReadOAuthReturn(state) ?? "/");
            var oauthPolicy = service.Match(context.Request.Host.Host, returnUrl);
            if (oauthPolicy == null || !oauthPolicy.Mode.Equals("OAuth2", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 401; return; }
            var exchange = await service.ExchangeOAuthAsync(context.Request.Query["code"].ToString(), state, oauthPolicy, ip, context.RequestAborted);
            if (exchange == null) { context.Response.StatusCode = 401; return; }
            WriteCookie(context, service.Protect(exchange.Identity)); context.Response.Redirect(SafeReturn(exchange.ReturnUrl)); return;
        }
        var policy = service.Match(context);
        if (policy == null) { await next(context); return; }
        if (service.IsIpExcepted(policy, ip)) { await next(context); return; }
        if (policy.Mode.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            var credentials = ParseBasic(context.Request.Headers.Authorization);
            if (credentials != null && service.Authenticate(credentials.Value.User, credentials.Value.Password, policy, ip) != null)
            { await next(context); return; }
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"LyWaf protected site\", charset=\"UTF-8\"";
            context.Response.StatusCode = service.IsLimited(ip) ? 429 : 401; return;
        }
        var current = options.CurrentValue;
        if (service.Unprotect(context.Request.Cookies[current.CookieName], policy) != null) { await next(context); return; }
        if (policy.Mode.Equals("OAuth2", StringComparison.OrdinalIgnoreCase) && current.OAuth2.Enabled)
        {
            var oauth = current.OAuth2; var state = service.OAuthState(context.Request.Path + context.Request.QueryString,
                DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
            var url = oauth.AuthorizationEndpoint + "?response_type=code&client_id=" + Uri.EscapeDataString(oauth.ClientId) +
                "&redirect_uri=" + Uri.EscapeDataString(oauth.RedirectUri) + "&scope=" + Uri.EscapeDataString(oauth.Scope) + "&state=" + Uri.EscapeDataString(state);
            context.Response.Redirect(url); return;
        }
        await WriteLogin(context, null, context.Request.Path + context.Request.QueryString);
    }
    private void WriteCookie(HttpContext context, string value)
    {
        var current = options.CurrentValue;
        context.Response.Cookies.Append(current.CookieName, value,
        new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(Math.Clamp(current.SessionMinutes, 1, 10080)), Path = "/" });
    }
    private static async Task WriteLogin(HttpContext context, string? error, string returnUrl)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var ret = HtmlEncoder.Default.Encode(SafeReturn(returnUrl));
        await context.Response.WriteAsync($"<!doctype html><meta charset=utf-8><title>登录</title><main><h1>访问认证</h1>{(error == null ? "" : "<p>" + error + "</p>")}<form method=post action=/.lywaf-site-auth/login><input type=hidden name=returnUrl value=\"{ret}\"><label>用户名<input name=username autocomplete=username required></label><label>密码<input name=password type=password autocomplete=current-password required></label><button type=submit>登录</button></form></main>");
    }
    private static string SafeReturn(string value) => value.StartsWith('/') && !value.StartsWith("//") ? value : "/";
    private static (string User, string Password)? ParseBasic(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!AuthenticationHeaderValue.TryParse(value, out var header) || !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)) return null;
        try { var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter ?? "")); var split = raw.IndexOf(':'); return split > 0 ? (raw[..split], raw[(split + 1)..]) : null; }
        catch (FormatException) { return null; }
    }
}
