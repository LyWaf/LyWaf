using System.Text;
using LyWaf.Config;
using LyWaf.Services.RequestMirroring;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using LyWaf.Services.Metrics;
using Xunit;

namespace LyWaf.Tests;

public sealed class RequestMirroringTests
{
    [Fact]
    public void PolicyIsSafeByDefaultAndAcceptsExplicitReplayMethods()
    {
        var policy = RequestMirrorPolicy.FromMetadata(new Dictionary<string, string>
        {
            ["Mirror.Enabled"] = "true", ["Mirror.Target"] = "https://shadow.example/base/",
            ["Mirror.Methods"] = "GET,POST", ["Mirror.Percentage"] = "25"
        });

        Assert.True(policy.Enabled);
        Assert.Contains("POST", policy.Methods);
        Assert.DoesNotContain("Authorization", policy.AllowedHeaders);
        Assert.Equal(25, policy.Percentage);
    }

    [Fact]
    public async Task SnapshotCopiesBoundedBodyAndOnlyAllowedHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/orders";
        context.Request.QueryString = new QueryString("?dry=true");
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = "Bearer secret";
        context.Request.Headers.Accept = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}"));
        context.Request.ContentLength = context.Request.Body.Length;
        var policy = RequestMirrorPolicy.FromMetadata(new Dictionary<string, string>
        {
            ["Mirror.Enabled"] = "true", ["Mirror.Target"] = "https://shadow.example/",
            ["Mirror.Methods"] = "POST"
        });

        var snapshot = await RequestMirrorMiddleware.CreateSnapshotAsync(context, policy, default);

        Assert.NotNull(snapshot);
        Assert.Equal("https://shadow.example/orders?dry=true", snapshot.Target.ToString());
        Assert.Equal("{\"id\":1}", Encoding.UTF8.GetString(snapshot.Body!));
        Assert.Equal(0, context.Request.Body.Position);
        Assert.Contains("Accept", snapshot.Headers.Keys);
        Assert.DoesNotContain("Authorization", snapshot.Headers.Keys);
        Assert.Equal("true", Assert.Single(snapshot.Headers["X-LyWaf-Mirror"]));
    }

    [Fact]
    public void LyConfigWritesPathMirrorMetadata()
    {
        var parsed = LyConfigParser.Parse("""
            example.test {
                /api/* {
                    Mirror {
                        Target = "https://shadow.example"
                        Percentage = 10
                        Methods = ["GET", "POST"]
                        MaxBodyBytes = 4096
                    }
                    reverse_proxy http://127.0.0.1:8080
                }
            }
            """);
        var appSettings = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var proxy = Assert.IsType<Dictionary<string, object>>(appSettings["ReverseProxy"]);
        var routes = Assert.IsType<Dictionary<string, object>>(proxy["Routes"]);
        var route = Assert.IsType<Dictionary<string, object>>(Assert.Single(routes).Value);
        var metadata = Assert.IsType<Dictionary<string, object>>(route["Metadata"]);

        Assert.Equal("https://shadow.example", metadata["Mirror.Target"]);
        Assert.Equal("10", metadata["Mirror.Percentage"]);
        Assert.Equal("GET,POST", metadata["Mirror.Methods"]);
        Assert.Equal("4096", metadata["Mirror.MaxBodyBytes"]);
    }

    [Fact]
    public async Task DispatcherConsumesQueuedRequestWithoutExposingFailure()
    {
        var handler = new RecordingHandler();
        var dispatcher = new RequestMirrorDispatcher(new ClientFactory(handler),
            Options.Create(new RequestMirroringOptions { Workers = 1, QueueCapacity = 2, TimeoutSeconds = 1 }),
            new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));
        await dispatcher.StartAsync(default);
        Assert.True(dispatcher.TryEnqueue(new MirroredRequest(HttpMethod.Get,
            new Uri("https://shadow.example/test"), new Dictionary<string, string[]>(), null, null)));
        await handler.Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(default);
        Assert.Equal(1, handler.Count);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Count;
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            Sent.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
