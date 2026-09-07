using LyWaf.Services.Lifecycle;
using Xunit;

namespace LyWaf.Tests;

/// <summary>
/// 验证实例排空时的新请求拒绝、在途计数和超时行为。
/// </summary>
public sealed class GracefulShutdownCoordinatorTests
{
    [Fact]
    public async Task DrainWaitsForExistingRequestsAndRejectsNewRequests()
    {
        var coordinator = new GracefulShutdownCoordinator();
        coordinator.MarkReady();
        Assert.True(coordinator.TryEnter(webSocket: false));
        Assert.True(coordinator.TryEnter(webSocket: true));

        coordinator.BeginDrain();
        Assert.False(coordinator.TryEnter(webSocket: false));
        Assert.False(coordinator.Snapshot().Ready);

        var waiting = coordinator.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);
        coordinator.Exit(webSocket: false);
        coordinator.Exit(webSocket: true);

        Assert.True(await waiting);
        Assert.Equal(0, coordinator.Snapshot().ActiveRequests);
        Assert.Equal(0, coordinator.Snapshot().ActiveWebSockets);
    }

    [Fact]
    public async Task DrainReturnsFalseWhenRequestExceedsTimeout()
    {
        var coordinator = new GracefulShutdownCoordinator();
        Assert.True(coordinator.TryEnter(webSocket: false));
        coordinator.BeginDrain();

        Assert.False(await coordinator.WaitForDrainAsync(TimeSpan.FromMilliseconds(20)));
        coordinator.Exit(webSocket: false);
    }
}
