using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcBanCoordinatorTests
{
    [Fact]
    public void ConcurrentDetectionsApplyAndLogOnlyOneBan()
    {
        var log = new CcBanLog();
        var coordinator = new CcBanCoordinator(log);
        var blocked = false;
        var applied = 0;
        Parallel.For(0, 100, _ => coordinator.TryBan("127.0.0.1", "CC", TimeSpan.FromMinutes(10),
            () => blocked, () => { applied++; blocked = true; }));
        Assert.Equal(1, applied);
        Assert.Single(log.Read());
    }

    [Fact]
    public void LiveUnblockOrExpiryAllowsANewBanWithoutRefreshingExistingBan()
    {
        var log = new CcBanLog();
        var coordinator = new CcBanCoordinator(log);
        var blocked = false;
        bool Ban() => coordinator.TryBan("::1", "CC", TimeSpan.FromSeconds(60),
            () => blocked, () => blocked = true);
        Assert.True(Ban());
        Assert.False(Ban());
        blocked = false;
        Assert.True(Ban());
        Assert.Equal(2, log.Read().Length);
    }

    [Fact]
    public void ExistingBanIsNotLoggedAsNewCcBan()
    {
        var log = new CcBanLog();
        var coordinator = new CcBanCoordinator(log);
        Assert.False(coordinator.TryBan("::1", "CC", TimeSpan.FromSeconds(60), () => true,
            () => throw new InvalidOperationException()));
        Assert.Empty(log.Read());
    }
}
