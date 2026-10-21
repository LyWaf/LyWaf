using LyWaf.Services.Metrics;
using LyWaf.Services.RequestMirroring;
using LyWaf.Services.ResponseCaching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class BackgroundQueueShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheRefreshStopsNormallyWhileIdleOrSending(bool inFlight)
    {
        using var factory = new BlockingFactory();
        using var service = new ResponseCacheRefreshService(factory);
        var request = new ResponseCacheRefreshRequest("fixture", new Uri("http://fixture.invalid/"), new Dictionary<string, string>());
        if (inFlight) Assert.True(service.TryQueue(request));
        await service.StartAsync(default);
        if (inFlight) await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopNormally(service);
        Assert.False(service.TryQueue(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MirrorWorkersStopNormallyWhileIdleOrSending(bool inFlight)
    {
        using var factory = new BlockingFactory();
        using var service = new RequestMirrorDispatcher(factory, Options.Create(new RequestMirroringOptions { Workers = 2 }),
            new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));
        var request = new MirroredRequest(HttpMethod.Get, new Uri("http://fixture.invalid/"), new Dictionary<string, string[]>(), null, null);
        if (inFlight) Assert.True(service.TryEnqueue(request));
        await service.StartAsync(default);
        if (inFlight) await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopNormally(service);
        Assert.False(service.TryEnqueue(request));
    }

    private static async Task StopNormally(BackgroundService service)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(deadline.Token);
        Assert.NotNull(service.ExecuteTask);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class BlockingFactory : HttpMessageHandler, IHttpClientFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
