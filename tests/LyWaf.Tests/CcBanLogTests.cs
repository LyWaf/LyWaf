using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcBanLogTests
{
    [Fact]
    public void RetainsLatestThousandInReverseOrder()
    {
        var log = new CcBanLog();
        for (var i = 0; i < 1100; i++) log.Add("127.0.0.1", i.ToString(), TimeSpan.FromSeconds(60));
        var entries = log.Read();
        Assert.Equal(1000, entries.Length);
        Assert.Equal("1099", entries[0].Reason);
        Assert.Equal("100", entries[^1].Reason);
        Assert.Equal(60, entries[0].DurationSeconds);
    }

    [Fact]
    public void ConcurrentWritesStayBoundedAndSnapshotsAreIndependent()
    {
        var log = new CcBanLog();
        Parallel.For(0, 2000, i => log.Add("::1", new string('a', 2000), TimeSpan.FromSeconds(1)));
        var entries = log.Read();
        Assert.Equal(1000, entries.Length);
        Assert.Equal(1000, entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(entries, entry => Assert.Equal(1024, entry.Reason.Length));
        log.Add("new", "new", TimeSpan.Zero);
        Assert.DoesNotContain(entries, entry => entry.Ip == "new");
    }
}
