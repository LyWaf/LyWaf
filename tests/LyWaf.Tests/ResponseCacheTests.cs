using System.Text;
using LyWaf.Services.ResponseCaching;
using LyWaf.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class ResponseCacheTests
{
    [Fact]
    public void LyConfigBindsGlobalOptionsAndRouteMetadata()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tests", "fixtures", "response-cache.ly");
        var configuration = new ConfigurationBuilder().AddLyConfig(path, optional: false).Build();
        var options = configuration.GetSection("ResponseCache").Get<ResponseCacheOptions>();
        var route = Assert.Single(configuration.GetSection("ReverseProxy:Routes").GetChildren());

        Assert.NotNull(options);
        Assert.False(options.Enabled);
        Assert.Equal(4096, options.MaxBodyBytes);
        Assert.Equal("True", route["Metadata:ResponseCache.Enabled"]);
        Assert.Equal("120", route["Metadata:ResponseCache.TtlSeconds"]);
    }

    [Fact]
    public async Task MissWritesDiskAndSecondRequestHitsWithoutCallingUpstream()
    {
        using var scope = new CacheScope();
        var calls = 0;
        var middleware = scope.Middleware(async context =>
        {
            calls++;
            context.Response.ContentType = "text/plain";
            context.Response.Headers.ETag = "\"v1\"";
            await context.Response.WriteAsync("cached-body");
        });

        var first = await Invoke(middleware, "/asset?a=1");
        var second = await Invoke(middleware, "/asset?a=1");

        Assert.Equal("cached-body", first.Body);
        Assert.Equal("MISS", first.CacheStatus);
        Assert.Equal("cached-body", second.Body);
        Assert.Equal("HIT", second.CacheStatus);
        Assert.Equal(1, calls);
        var entry = Assert.Single(scope.Store.GetSnapshots());
        Assert.True(File.Exists(Path.Combine(scope.Directory, $"{entry.Key}.body")));
    }

    [Fact]
    public async Task VaryCreatesIndependentVariants()
    {
        using var scope = new CacheScope();
        var calls = 0;
        var middleware = scope.Middleware(async context =>
        {
            calls++;
            context.Response.Headers.Vary = "Accept-Language";
            await context.Response.WriteAsync(context.Request.Headers.AcceptLanguage.ToString());
        });

        Assert.Equal("zh-CN", (await Invoke(middleware, "/vary", headers: new() { ["Accept-Language"] = "zh-CN" })).Body);
        Assert.Equal("en-US", (await Invoke(middleware, "/vary", headers: new() { ["Accept-Language"] = "en-US" })).Body);
        Assert.Equal("HIT", (await Invoke(middleware, "/vary", headers: new() { ["Accept-Language"] = "zh-CN" })).CacheStatus);
        Assert.Equal(2, calls);
        Assert.Equal(2, scope.Store.GetSnapshots().Count);
    }

    [Fact]
    public async Task CachedEtagAnswersClientConditionalRequest()
    {
        using var scope = new CacheScope();
        var middleware = scope.Middleware(async context =>
        {
            context.Response.Headers.ETag = "\"abc\"";
            await context.Response.WriteAsync("body");
        });
        await Invoke(middleware, "/etag");

        var result = await Invoke(middleware, "/etag", headers: new() { ["If-None-Match"] = "\"abc\"" });

        Assert.Equal(304, result.StatusCode);
        Assert.Empty(result.Body);
        Assert.Equal("HIT", result.CacheStatus);
    }

    [Fact]
    public async Task HeadReusesGetMetadataWithoutWritingBody()
    {
        using var scope = new CacheScope();
        var calls = 0;
        var middleware = scope.Middleware(async context => { calls++; await context.Response.WriteAsync("content"); });
        await Invoke(middleware, "/head");

        var result = await Invoke(middleware, "/head", method: "HEAD");

        Assert.Equal(1, calls);
        Assert.Empty(result.Body);
        Assert.Equal("7", result.ContentLength);
        Assert.Equal("HIT", result.CacheStatus);
    }

    [Theory]
    [InlineData("Authorization", "Bearer token")]
    [InlineData("Range", "bytes=0-1")]
    [InlineData("Cache-Control", "no-store")]
    public async Task SensitiveOrPartialRequestsBypassCache(string name, string value)
    {
        using var scope = new CacheScope();
        var calls = 0;
        var middleware = scope.Middleware(async context => { calls++; await context.Response.WriteAsync(calls.ToString()); });

        var headers = new Dictionary<string, string> { [name] = value };
        await Invoke(middleware, "/private", headers: headers);
        await Invoke(middleware, "/private", headers: headers);

        Assert.Equal(2, calls);
        Assert.Empty(scope.Store.GetSnapshots());
    }

    [Fact]
    public async Task ConcurrentMissesUseSingleUpstreamRequest()
    {
        using var scope = new CacheScope();
        var calls = 0;
        var middleware = scope.Middleware(async context =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100);
            await context.Response.WriteAsync("one");
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Invoke(middleware, "/hot")));

        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.Equal("one", result.Body));
        Assert.Equal(0, scope.Store.ActiveLockCount);
    }

    [Fact]
    public async Task ExpiredEntryRevalidatesWithEtagAndUsesBodyOn304()
    {
        using var scope = new CacheScope(staleWhileRevalidateSeconds: 0);
        var calls = 0;
        var middleware = scope.Middleware(async context =>
        {
            calls++;
            if (calls == 2)
            {
                Assert.Equal("\"v1\"", context.Request.Headers.IfNoneMatch);
                context.Response.StatusCode = 304;
                return;
            }
            context.Response.Headers.CacheControl = "max-age=0";
            context.Response.Headers.ETag = "\"v1\"";
            await context.Response.WriteAsync("stable");
        });

        await Invoke(middleware, "/revalidate");
        var result = await Invoke(middleware, "/revalidate");

        Assert.Equal("stable", result.Body);
        Assert.Equal("REVALIDATED", result.CacheStatus);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StaleWhileRevalidateReturnsImmediatelyAndQueuesOneRefresh()
    {
        var factory = new RecordingHttpClientFactory();
        using var scope = new CacheScope(clientFactory: factory);
        await scope.StartRefreshAsync();
        var calls = 0;
        var middleware = scope.Middleware(async context =>
        {
            calls++;
            context.Response.Headers.CacheControl = "max-age=0";
            await context.Response.WriteAsync("old");
        });
        await Invoke(middleware, "/swr");

        var first = await Invoke(middleware, "/swr");
        var second = await Invoke(middleware, "/swr");
        await factory.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("STALE", first.CacheStatus);
        Assert.Equal("STALE", second.CacheStatus);
        Assert.Equal(1, calls);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task StaleVaryVariantsQueueIndependentRefreshes()
    {
        var factory = new RecordingHttpClientFactory();
        using var scope = new CacheScope(clientFactory: factory);
        await scope.StartRefreshAsync();
        var middleware = scope.Middleware(async context =>
        {
            context.Response.Headers.CacheControl = "max-age=0";
            context.Response.Headers.Vary = "Accept-Language";
            await context.Response.WriteAsync(context.Request.Headers.AcceptLanguage.ToString());
        });
        await Invoke(middleware, "/variants", headers: new() { ["Accept-Language"] = "zh-CN" });
        await Invoke(middleware, "/variants", headers: new() { ["Accept-Language"] = "en-US" });

        await Invoke(middleware, "/variants", headers: new() { ["Accept-Language"] = "zh-CN" });
        await Invoke(middleware, "/variants", headers: new() { ["Accept-Language"] = "en-US" });
        await WaitForAsync(() => Volatile.Read(ref factory.Calls) == 2);

        Assert.Equal(2, factory.Calls);
    }

    [Fact]
    public async Task UpstreamFailureServesStaleEntry()
    {
        using var scope = new CacheScope(staleWhileRevalidateSeconds: 0);
        var calls = 0;
        var middleware = scope.Middleware(async context =>
        {
            calls++;
            context.Response.Headers.CacheControl = "max-age=0";
            if (calls == 1) await context.Response.WriteAsync("last-good");
            else { context.Response.StatusCode = 503; await context.Response.WriteAsync("failure"); }
        });

        await Invoke(middleware, "/stale");
        var result = await Invoke(middleware, "/stale");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("last-good", result.Body);
        Assert.Equal("STALE-ERROR", result.CacheStatus);
    }

    [Fact]
    public async Task OversizedResponseStreamsThroughWithoutCacheEntry()
    {
        using var scope = new CacheScope(maxBodyBytes: 4);
        var middleware = scope.Middleware(context => context.Response.WriteAsync("123456789"));

        var result = await Invoke(middleware, "/large");

        Assert.Equal("123456789", result.Body);
        Assert.Empty(scope.Store.GetSnapshots());
        Assert.Empty(Directory.EnumerateFiles(scope.Directory));
    }

    [Fact]
    public async Task EntryLargerThanTotalCapacityStreamsWithoutBeingCommitted()
    {
        using var scope = new CacheScope(maxBodyBytes: 1024, maxCacheBytes: 4);
        var middleware = scope.Middleware(context => context.Response.WriteAsync("123456789"));

        var result = await Invoke(middleware, "/capacity");

        Assert.Equal("123456789", result.Body);
        Assert.Empty(scope.Store.GetSnapshots());
    }

    [Fact]
    public async Task PurgeSupportsHostPathAndExactKey()
    {
        using var scope = new CacheScope();
        var middleware = scope.Middleware(context => context.Response.WriteAsync(context.Request.Path));
        await Invoke(middleware, "/api/a");
        await Invoke(middleware, "/static/a");

        Assert.Equal(1, scope.Store.Purge(new(Host: "example.test", Path: "/api")));
        var remaining = Assert.Single(scope.Store.GetSnapshots());
        Assert.Equal(1, scope.Store.Purge(new(Key: remaining.Key)));
        Assert.Empty(scope.Store.GetSnapshots());
    }

    private static async Task<Result> Invoke(ResponseCacheMiddleware middleware, string target,
        string method = "GET", Dictionary<string, string>? headers = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("example.test");
        var question = target.IndexOf('?');
        context.Request.Path = question < 0 ? target : target[..question];
        context.Request.QueryString = question < 0 ? QueryString.Empty : new QueryString(target[question..]);
        foreach (var pair in headers ?? []) context.Request.Headers[pair.Key] = pair.Value;
        var body = new MemoryStream();
        context.Response.Body = body;
        await middleware.InvokeAsync(context);
        body.Position = 0;
        return new(context.Response.StatusCode, await new StreamReader(body, Encoding.UTF8).ReadToEndAsync(),
            context.Response.Headers["X-LyWaf-Cache"].ToString(), context.Response.Headers.ContentLength.ToString() ?? string.Empty);
    }

    private sealed record Result(int StatusCode, string Body, string CacheStatus, string ContentLength);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class CacheScope : IDisposable
    {
        private readonly StaticOptionsMonitor<ResponseCacheOptions> _monitor;
        private readonly ResponseCacheRefreshService _refresh;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"lywaf-cache-test-{Guid.NewGuid():N}");
        public ResponseCacheStore Store { get; }

        public CacheScope(long maxBodyBytes = 1024, int staleWhileRevalidateSeconds = 30,
            long maxCacheBytes = 1024 * 1024, IHttpClientFactory? clientFactory = null)
        {
            _monitor = new(new ResponseCacheOptions
            {
                Enabled = true,
                Directory = Directory,
                MaxBodyBytes = maxBodyBytes,
                MaxCacheBytes = maxCacheBytes,
                DefaultTtlSeconds = 60,
                StaleWhileRevalidateSeconds = staleWhileRevalidateSeconds,
                StaleIfErrorSeconds = 300
            });
            Store = new(_monitor);
            _refresh = new(clientFactory ?? new NoopHttpClientFactory());
        }

        public ResponseCacheMiddleware Middleware(RequestDelegate next) => new(next, Store, _refresh, _monitor);
        public Task StartRefreshAsync() => _refresh.StartAsync(CancellationToken.None);
        public void Dispose()
        {
            _refresh.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Store.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public int Calls;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(RecordingHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Calls);
                owner.Called.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }
        }
    }
}
