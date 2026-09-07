using System.Security.Cryptography;
using LyWaf.Plugins.Sys.SiteAuth;
using LyWaf.Services.AuditLog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class SiteAuthTests
{
    [Fact]
    public void MatchesHostAndPathAndHonorsIpException()
    {
        var policy = new SiteAuthPolicy { Host = "example.test", Path = "/private/**", IpExceptions = ["10.0.0.1"] };
        var service = Service(new SiteAuthOptions { SessionSecret = "site-secret", Policies = [policy] });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("example.test"); context.Request.Path = "/private/report";

        Assert.Same(policy, service.Match(context));
        Assert.True(service.IsIpExcepted(policy, "10.0.0.1"));
        context.Request.Path = "/public";
        Assert.Null(service.Match(context));
    }

    [Fact]
    public void AuthenticatesPbkdf2UserAndBindsAuthorizationGroupToSession()
    {
        var policy = new SiteAuthPolicy { AllowedGroups = ["finance"] };
        var service = Service(new SiteAuthOptions { SessionSecret = "independent-site-secret",
            Users = [new SiteAuthUser { Username = "alice", PasswordHash = Hash("correct"), Groups = ["finance"] }] });

        var identity = service.Authenticate("alice", "correct", policy, "127.0.0.1");
        Assert.NotNull(identity);
        var token = service.Protect(identity!);
        Assert.Equal("alice", service.Unprotect(token, policy)?.Username);
        Assert.Null(service.Unprotect(token + "tampered", policy));
        Assert.Null(service.Unprotect(token, new SiteAuthPolicy { AllowedGroups = ["admin"] }));
    }

    [Fact]
    public void LimitsRepeatedFailuresPerClient()
    {
        var service = Service(new SiteAuthOptions { SessionSecret = "site-secret", MaxFailuresPerMinute = 2 });
        var policy = new SiteAuthPolicy();

        Assert.Null(service.Authenticate("missing", "bad", policy, "192.0.2.1"));
        Assert.Null(service.Authenticate("missing", "bad", policy, "192.0.2.1"));
        Assert.True(service.IsLimited("192.0.2.1"));
    }

    [Fact]
    public async Task FormCallbackUsesOriginalProtectedPathPolicy()
    {
        var options = new SiteAuthOptions { SessionSecret = "independent-site-secret",
            Policies = [new SiteAuthPolicy { Host = "example.test", Path = "/private/**", Mode = "Form" }],
            Users = [new SiteAuthUser { Username = "alice", PasswordHash = Hash("correct") }] };
        var monitor = new MutableOptionsMonitor<SiteAuthOptions>(options);
        var service = Service(monitor);
        var middleware = new SiteAuthMiddleware(_ => Task.CompletedTask, service, monitor);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/.lywaf-site-auth/login";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "username=alice&password=correct&returnUrl=%2Fprivate%2Freport"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/private/report", context.Response.Headers.Location);
        Assert.Contains("LyWafSiteAuth", context.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void ReloadedOptionsAreUsedWithoutRestart()
    {
        var monitor = new MutableOptionsMonitor<SiteAuthOptions>(new());
        var service = Service(monitor);
        Assert.Null(service.Match("example.test", "/private/a"));

        monitor.Set(new SiteAuthOptions { Policies = [new SiteAuthPolicy { Host = "example.test", Path = "/private/**" }] });

        Assert.NotNull(service.Match("example.test", "/private/a"));
    }

    private static SiteAuthService Service(SiteAuthOptions options) => Service(new MutableOptionsMonitor<SiteAuthOptions>(options));
    private static SiteAuthService Service(IOptionsMonitor<SiteAuthOptions> options) =>
        new(options, new EmptyClients(), new Audit());

    private static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private sealed class EmptyClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
    private sealed class Audit : IAuditLogService
    {
        public void Log(string username, string action, string ip) { }
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }
    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private T _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
        public void Set(T next) => _value = next;
    }
}
