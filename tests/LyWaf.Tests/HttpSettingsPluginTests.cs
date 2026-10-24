using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Config;
using LyWaf.Plugins.Sys.HttpSettings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class HttpSettingsPluginTests
{
    [Fact]
    public void ResponseDefaultsAreDeclaredWithResponseOperations()
    {
        var fields = PluginConfigurationSchema.Describe(typeof(HttpSettingsOptions)).Select(field => field.Path).ToList();
        Assert.True(fields.IndexOf(nameof(HttpSettingsOptions.RemoveRequestHeaders)) < fields.IndexOf(nameof(HttpSettingsOptions.Headers)));
        Assert.True(fields.IndexOf(nameof(HttpSettingsOptions.Headers)) < fields.IndexOf(nameof(HttpSettingsOptions.ResponseHeaders)));
    }

    [Fact]
    public void NativeExampleUsesRealConfigurationProvider()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Plugins/Sys/HttpSettings/example.ly"));
        var configuration = new ConfigurationBuilder().Add(new LyConfigSource { FilePath = path, Optional = false }).Build();
        using var resolver = new PluginScopedConfiguration(configuration, [new HttpSettingsPlugin().Metadata], NullLogger<PluginScopedConfiguration>.Instance);
        var context = new DefaultHttpContext(); context.Request.Host = new("example.com"); context.Request.Path = "/api/test";
        var options = resolver.Get<HttpSettingsOptions>(context, "http-settings");
        Assert.True(options.SecurityHeaders);
        Assert.Equal("X-Request-ID", options.RequestIdHeader);
        Assert.Equal("no-store", options.ResponseHeaders["Cache-Control"]);
        Assert.Contains("X-Powered-By", options.RemoveResponseHeaders);
    }

    [Theory]
    [InlineData("StatusCodes:0", "2*")]
    [InlineData("HstsMaxAgeSeconds", "-1")]
    [InlineData("RequestIdHeader", "Authorization")]
    [InlineData("RequestIdHeader", "traceparent")]
    public void InvalidPolicyFieldsAreRejected(string path, string value) => Assert.NotEmpty(
        PluginConfigurationSchema.Validate(typeof(HttpSettingsOptions), new Dictionary<string, string> { [path] = value }));

    [Fact]
    public void HeaderOperationsProtectConnectionNominatedFields()
    {
        var headers = new HeaderDictionary { ["Connection"] = "X-Hop", ["X-Hop"] = "original" };
        HttpHeaderPolicy.Apply(headers, new Dictionary<string, string>(), new Dictionary<string, string> { ["X-Hop"] = "changed" },
            new Dictionary<string, string>(), ["X-Hop"]);
        Assert.Equal("original", headers["X-Hop"]);
    }

    [Fact]
    public async Task LegacyHeadersOnlyFillAbsentValues()
    {
        await using var server = await Server.Start();
        using var response = await server.Client.GetAsync("/");
        Assert.Equal("LyWaf", response.Headers.GetValues("X-Powered-By").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("http-settings", new HttpSettingsPlugin().Metadata.Id);
    }

    [Fact]
    public async Task FirstStageRunsOnShortCircuitAndAfterInnerOnStarting()
    {
        await using var server = await Server.Start(o => o.ResponseHeaders["X-Result"] = "policy", context =>
        {
            context.Response.StatusCode = 403;
            context.Response.OnStarting(() => { context.Response.Headers["X-Result"] = "inner"; return Task.CompletedTask; });
            return context.Response.WriteAsync("blocked");
        });
        using var response = await server.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("policy", response.Headers.GetValues("X-Result").Single());
    }

    [Fact]
    public async Task RequestAndResponseOperationsHaveDeterministicPrecedence()
    {
        await using var server = await Server.Start(o =>
        {
            o.RequestHeadersIfMissing["X-Default"] = "fallback";
            o.RequestHeaders["X-Set"] = "new";
            o.AppendRequestHeaders["X-Append"] = "two";
            o.RemoveRequestHeaders.Add("X-Delete");
            o.Headers["X-Result"] = "default";
            o.ResponseHeaders["X-Result"] = "set";
            o.AppendResponseHeaders["X-Result"] = "append";
            o.ResponseHeaders["X-Removed"] = "set";
            o.RemoveResponseHeaders.Add("X-Removed");
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Default", "existing"); request.Headers.Add("X-Set", "old");
        request.Headers.Add("X-Append", "one"); request.Headers.Add("X-Delete", "remove");
        using var response = await server.Client.SendAsync(request);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        var headers = data.GetProperty("headers");
        Assert.Equal("existing", headers.GetProperty("X-Default").GetString());
        Assert.Equal("new", headers.GetProperty("X-Set").GetString());
        Assert.Contains("one", headers.GetProperty("X-Append").GetString());
        Assert.Contains("two", headers.GetProperty("X-Append").GetString());
        Assert.False(headers.TryGetProperty("X-Delete", out _));
        Assert.Equal(["set", "append"], response.Headers.GetValues("X-Result"));
        Assert.False(response.Headers.Contains("X-Removed"));
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(403, true)]
    [InlineData(499, true)]
    [InlineData(503, false)]
    public async Task ResponseStatusFiltersSupportWildcards(int status, bool present)
    {
        await using var server = await Server.Start(o => { o.StatusCodes = ["4xx"]; o.ResponseHeaders["X-Filtered"] = "yes"; }, context =>
        { context.Response.StatusCode = status; return context.Response.WriteAsync("fixture"); });
        using var response = await server.Client.GetAsync("/");
        Assert.Equal(present, response.Headers.Contains("X-Filtered"));
    }

    [Fact]
    public async Task DisabledPolicyDoesNotMutateTraffic()
    {
        await using var server = await Server.Start(o => { o.Enabled = false; o.RequestHeaders["X-Added"] = "yes"; });
        using var response = await server.Client.GetAsync("/");
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headers").TryGetProperty("X-Added", out _));
    }

    [Fact]
    public async Task RequestIdIgnoresSpoofedInputAndIsStableAcrossBothDirections()
    {
        await using var server = await Server.Start(o => o.RequestIdHeader = "X-Request-ID");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/"); request.Headers.Add("X-Request-ID", "spoofed");
        using var response = await server.Client.SendAsync(request);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = response.Headers.GetValues("X-Request-ID").Single();
        Assert.NotEqual("spoofed", id);
        Assert.Equal(data.GetProperty("trace").GetString(), id);
        Assert.Equal(data.GetProperty("headers").GetProperty("X-Request-ID").GetString(), id);
    }

    [Fact]
    public async Task SecurityDefaultsAreOptInAndHstsIsNotSentOverHttp()
    {
        await using var server = await Server.Start(o => { o.SecurityHeaders = true; o.HstsMaxAgeSeconds = 3600; });
        using var response = await server.Client.GetAsync("/");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task HstsIsAppliedOnlyWhenRequestIsHttps()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
        var plugin = new HttpSettingsPlugin(); ((HttpSettingsOptions)plugin.Metadata.DefaultOptions!).HstsMaxAgeSeconds = 3600;
        plugin.ConfigureProxyStage(app, PluginProxyStage.First);
        app.Run(context => context.Response.WriteAsync("secure fixture"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var response = await client.GetAsync("/");
        Assert.Equal("max-age=3600", response.Headers.GetValues("Strict-Transport-Security").Single());
        await app.StopAsync();
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Host")]
    [InlineData("Connection")]
    [InlineData("Content-Encoding")]
    [InlineData("X-Forwarded-For")]
    [InlineData("bad\r\nname")]
    [InlineData(":status")]
    public void ProtectedOrInvalidHeadersAreRejected(string name)
    {
        Assert.False(HttpHeaderPolicy.IsAllowedName(name));
        Assert.NotEmpty(PluginConfigurationSchema.Validate(typeof(HttpSettingsOptions), new Dictionary<string, string> { ["ResponseHeaders:" + name] = "value" }));
    }

    [Theory]
    [InlineData("value\r\nX-Injected: yes")]
    [InlineData("bad\0value")]
    [InlineData("中文")]
    public void UnsafeValuesAreRejected(string value)
    {
        Assert.False(HttpHeaderPolicy.IsValidValue(value));
        Assert.NotEmpty(PluginConfigurationSchema.Validate(typeof(HttpSettingsOptions), new Dictionary<string, string> { ["ResponseHeaders:X-Test"] = value }));
    }

    [Theory]
    [InlineData("20x", 209, true)]
    [InlineData("30x", 308, true)]
    [InlineData("503", 500, false)]
    [InlineData("*", 101, true)]
    [InlineData("2*", 200, false)]
    public void StatusPatterns(string pattern, int code, bool expected) => Assert.Equal(expected, HttpHeaderPolicy.MatchesStatus(pattern, code));

    [Fact]
    public async Task InvalidDirectSettingsCannotChangeFramingOrInjectHeaders()
    {
        await using var server = await Server.Start(o =>
        {
            o.RequestHeaders["Host"] = "attacker.example";
            o.ResponseHeaders["Content-Length"] = "9999";
            o.ResponseHeaders["X-Bad"] = "value\r\nInjected: yes";
            o.RemoveResponseHeaders.Add("Content-Type");
        });
        using var response = await server.Client.GetAsync("/");
        Assert.False(response.Headers.Contains("X-Bad"));
        Assert.NotEqual(9999, response.Content.Headers.ContentLength);
        var data = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.NotEqual("attacker.example", data.GetProperty("headers").GetProperty("Host").GetString());
    }

    [Fact]
    public async Task SiteAndPathOverridesUseExistingScopedConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:http-settings:ResponseHeaders:X-Scope"] = "global",
            ["Sites:site:Hosts:0"] = "example.com",
            ["Sites:site:Plugins:http-settings:ResponseHeaders:X-Scope"] = "site",
            ["Sites:site:Routes:api:Path"] = "/api/**",
            ["Sites:site:Routes:api:Plugins:http-settings:ResponseHeaders:X-Scope"] = "path"
        }).Build();
        using var resolver = new PluginScopedConfiguration(configuration, [new HttpSettingsPlugin().Metadata], NullLogger<PluginScopedConfiguration>.Instance);
        await using var server = await Server.Start(resolver: resolver);
        foreach (var (host, path, expected) in new[] { ("other.com", "/", "global"), ("example.com", "/", "site"), ("example.com", "/api/item", "path") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Host = host;
            using var response = await server.Client.SendAsync(request);
            Assert.Equal(expected, response.Headers.GetValues("X-Scope").Single());
        }
    }

    private sealed class Server(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public static async Task<Server> Start(Action<HttpSettingsOptions>? configure = null, RequestDelegate? terminal = null, IPluginRequestConfiguration? resolver = null)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            if (resolver is not null) builder.Services.AddSingleton(resolver);
            var app = builder.Build();
            var plugin = new HttpSettingsPlugin(); configure?.Invoke((HttpSettingsOptions)plugin.Metadata.DefaultOptions!);
            plugin.ConfigureProxyStage(app, PluginProxyStage.First);
            app.Run(terminal ?? (async context =>
            {
                context.Response.Headers["X-Frame-Options"] = "DENY";
                await context.Response.WriteAsJsonAsync(new { headers = context.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToString()), trace = context.TraceIdentifier });
            }));
            await app.StartAsync();
            return new(app, new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) });
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
