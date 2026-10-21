using System.Reflection;
using LyWaf.Plugins.Core;
using LyWaf.Services.Metrics;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.WebTamper;
using Xunit;

namespace LyWaf.Tests;

public sealed class WebTamperShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueCancellationCompletesNormallyEvenWhenWaitingForTheFirstItem(bool alreadyCancelled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-tamper-stop-" + Guid.NewGuid().ToString("N"));
        using var state = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new() { StateDirectory = directory }));
        using var service = new WebTamperService(new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())), state);
        using var stopping = new CancellationTokenSource();
        if (alreadyCancelled) stopping.Cancel();
        var task = service.ProcessBackgroundChecksAsync(stopping.Token);
        if (!alreadyCancelled) Assert.False(task.IsCompleted);
        stopping.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HostedMonitorStopsWithBothWorkersObservedAndNoCancelledExecuteTask()
    {
        var queue = DispatchProxy.Create<IWebTamperService, CancellableQueue>();
        using var service = new WebTamperMonitorService(queue, null!);
        await service.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(deadline.Token);
        Assert.NotNull(service.ExecuteTask);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    public class CancellableQueue : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) =>
            method?.Name == nameof(IWebTamperService.ProcessBackgroundChecksAsync)
                ? Task.Delay(Timeout.Infinite, (CancellationToken)arguments![0]!)
                : throw new NotSupportedException(method?.Name);
    }
}
