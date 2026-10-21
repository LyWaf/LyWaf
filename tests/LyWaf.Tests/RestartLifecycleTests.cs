using System.Reflection;
using LyWaf.Services.Discovery;
using LyWaf.Services.DuckDb;
using LyWaf.Services.Lifecycle;
using LyWaf.Services.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RestartLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryStopsNormallyWhileIdleOrInsideAProvider(bool active)
    {
        var provider = new WaitingProvider();
        var settings = new ServiceDiscoveryOptions { Enabled = active };
        if (active) settings.Providers["fixture"] = new() { Type = provider.Type, ClusterId = "fixture" };
        using var service = new DiscoveryCoordinator(new StaticOptionsMonitor<ServiceDiscoveryOptions>(settings),
            [provider], new DiscoveryRegistry(), new DiscoveryReloadSignal());
        await service.StartAsync(default);
        if (active) await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopNormally(service);
    }

    [Fact]
    public async Task RetentionStopsNormallyDuringItsInitialFiveMinuteWait()
    {
        var db = DispatchProxy.Create<IDuckDbService, Database>();
        using var service = new DataRetentionService(db, Options.Create(new DuckDbOptions()), Metrics());
        await service.StartAsync(default);
        await StopNormally(service);
    }

    [Fact]
    public async Task FlushCancellationDuringStartupDoesNotDisableTheDatabase()
    {
        var db = DispatchProxy.Create<IDuckDbService, Database>();
        using var service = new StatisticFlushService(db, Options.Create(new DuckDbOptions()), Metrics());
        await service.StartAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Exercise cancellation before final-flush storage work, independently of a real database connection.
        service.Dispose();
        await service.ExecuteTask!.WaitAsync(stop.Token);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.True(db.IsEnabled);
    }

    [Fact]
    public async Task IdleWaitReportsCancellationWithoutThrowing()
    {
        using var stop = new CancellationTokenSource();
        var waiting = ShutdownWait.DelayAsync(TimeSpan.FromDays(1), stop.Token);
        stop.Cancel();
        Assert.False(await waiting);
        Assert.False(await ShutdownWait.DelayAsync(TimeSpan.FromSeconds(1), stop.Token));
        Assert.True(await ShutdownWait.DelayAsync(TimeSpan.Zero, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ShutdownWait.DelayAsync(TimeSpan.FromMilliseconds(-2), default));
    }

    private static PrometheusMetrics Metrics() => new(new StaticOptionsMonitor<MetricsOptions>(new()));
    private static async Task StopNormally(BackgroundService service)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(stop.Token);
        Assert.NotNull(service.ExecuteTask);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class WaitingProvider : IServiceDiscoveryProvider
    {
        public string Type => "Waiting";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(string id, DiscoveryProviderOptions options, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }
    }

    public class Database : DispatchProxy
    {
        private bool enabled = true;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsEnabled" => enabled,
            "set_IsEnabled" => enabled = (bool)args![0]!,
            nameof(IDuckDbService.InitializeSchemaAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected database operation: " + method?.Name)
        };
    }
}
