using System.Net;
using System.Text;
using LyWaf.Config;
using LyWaf.Services.ExternalAuthorization;
using LyWaf.Services.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class ExternalAuthorizationTests
{
    [Fact]
    public async Task RequestProjectionBoundsBodyAndFiltersHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.test");
        context.Request.Path = "/orders";
        context.Request.Headers.Authorization = "Bearer secret";
        context.Request.Headers["X-Untrusted"] = "omit";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        context.Request.ContentLength = 5;
        var settings = new ExternalAuthorizationOptions { MaxBodyBytes = 4 };
        var policy = ExternalAuthorizationPolicy.FromMetadata(new Dictionary<string, string>
        {
            ["ExtAuth.Enabled"] = "true", ["ExtAuth.Endpoint"] = "https://auth.example/check"
        }, settings);

        var projected = await ExternalAuthorizationMiddleware.BuildRequestAsync(context, policy, settings, default);

        Assert.True(projected.BodyTruncated);
        Assert.Null(projected.BodyBase64);
        Assert.Contains("Authorization", projected.Headers.Keys);
        Assert.DoesNotContain("X-Untrusted", projected.Headers.Keys);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public void CacheKeyHashesCredentialAndSeparatesUsers()
    {
        var first = Request("Bearer first");
        var second = Request("Bearer second");
        var firstKey = ExternalAuthorizationMiddleware.BuildCacheKey("route", first, ["Authorization"]);
        var secondKey = ExternalAuthorizationMiddleware.BuildCacheKey("route", second, ["Authorization"]);

        Assert.NotEqual(firstKey, secondKey);
        Assert.DoesNotContain("first", firstKey);
        Assert.Equal(64, firstKey.Length);
    }

    [Fact]
    public void CacheExpiresAndRemainsBounded()
    {
        var cache = new ExternalAuthorizationCache();
        var now = DateTimeOffset.UtcNow;
        cache.Set("a", new ExternalAuthorizationResponse(true), now, 1, 1);
        Assert.True(cache.TryGet("a", now, out _));
        Assert.False(cache.TryGet("a", now.AddSeconds(2), out _));
    }

    [Fact]
    public async Task AuthorizationResponseHasHardSizeLimit()
    {
        var handler = new StaticHandler(new string('x', 2048));
        var settings = new ExternalAuthorizationOptions { MaxResponseBytes = 1024 };
        var middleware = CreateMiddleware(handler, settings);
        var policy = new ExternalAuthorizationPolicy(true, new Uri("https://auth.example/check"), 1000, 0, false, 0);

        await Assert.ThrowsAsync<InvalidDataException>(() => middleware.AuthorizeAsync(policy, Request("token"), settings, default));
    }

    [Fact]
    public void LyConfigWritesExternalAuthorizationMetadata()
    {
        var parsed = LyConfigParser.Parse("""
            example.test {
                /private/* {
                    ExternalAuth { Endpoint = "https://auth.example/check" TimeoutMs = 500 FailOpen = false CacheSeconds = 10 }
                    reverse_proxy http://127.0.0.1:8080
                }
            }
            """);
        var app = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var proxy = Assert.IsType<Dictionary<string, object>>(app["ReverseProxy"]);
        var routes = Assert.IsType<Dictionary<string, object>>(proxy["Routes"]);
        var route = Assert.IsType<Dictionary<string, object>>(Assert.Single(routes).Value);
        var metadata = Assert.IsType<Dictionary<string, object>>(route["Metadata"]);
        Assert.Equal("https://auth.example/check", metadata["ExtAuth.Endpoint"]);
        Assert.Equal("500", metadata["ExtAuth.TimeoutMilliseconds"]);
        Assert.Equal("False", metadata["ExtAuth.FailOpen"]);
    }

    private static ExternalAuthorizationMiddleware CreateMiddleware(HttpMessageHandler handler, ExternalAuthorizationOptions settings) =>
        new(_ => Task.CompletedTask, new ClientFactory(handler), new StaticOptionsMonitor<ExternalAuthorizationOptions>(settings),
            new ExternalAuthorizationCache(), new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));

    private static ExternalAuthorizationRequest Request(string credential) => new("GET", "https", "api.example.test", "/",
        new Dictionary<string, string[]> { ["Authorization"] = [credential] }, null, false);

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
