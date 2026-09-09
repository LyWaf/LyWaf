using LyWaf.Services.AccessControl;
using Xunit;

namespace LyWaf.Tests;

public sealed class IpGroupUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lywaf-ip-group-tests-{Guid.NewGuid():N}");
    private string StorePath => Path.Combine(_directory, "groups.json");

    public IpGroupUsageTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Usage_IsPersisted_AndRaisesChangeNotification()
    {
        var service = new IpGroupService(StorePath);
        var group = service.Create("office", null, ["10.0.0.0/8"]);
        var changes = 0;
        service.Changed += () => changes++;

        var updated = service.SetUsage(group.Id, true, false);
        var reloaded = new IpGroupService(StorePath).GetAll().Single();

        Assert.True(updated!.WhitelistEnabled);
        Assert.True(reloaded.WhitelistEnabled);
        Assert.False(reloaded.BlacklistEnabled);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Group_CannotBeInBothLists()
    {
        var service = new IpGroupService(StorePath);
        var group = service.Create("blocked", null, ["192.0.2.1"]);

        Assert.Throws<ArgumentException>(() => service.SetUsage(group.Id, true, true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
