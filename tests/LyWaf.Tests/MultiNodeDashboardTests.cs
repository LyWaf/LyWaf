using LyWaf.Services.Distribution;
using LyWaf.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class MultiNodeDashboardTests
{
    [Theory]
    [InlineData("en-US", "This instance is not a multi-node controller")]
    [InlineData("zh-CN", "当前实例不是多节点控制器")]
    public async Task NonControllerErrorUsesRequestLanguage(string language, string expected)
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var app = new Microsoft.AspNetCore.Builder.ApplicationBuilder(services);
        app.UseManagementLocalization(7030, "zh-CN");
        var service = new MultiNodeDashboardService(
            new StaticOptionsMonitor<DistributionOptions>(new DistributionOptions()), new DashboardClient());
        app.Run(async context =>
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await service.GetAsync("admin", default); });
            var error = LyWaf.Services.ManagementApi.ManagementApiContract.CreateError(
                context, "distribution_not_controller", exception.Message);
            Assert.Equal(expected, error.Message);
            Assert.Equal(expected, error.Error.Message);
            Assert.Equal("distribution_not_controller", error.Error.Code);
        });
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.LocalPort = 7030;
        context.Request.Headers.AcceptLanguage = language;
        await app.Build()(context);
    }

    [Fact]
    public async Task AggregatesSecurityDeduplicatesEventsAndFlagsClockSkew()
    {
        var options = Options();
        var eventTime = DateTimeOffset.UtcNow;
        var client = new DashboardClient
        {
            Snapshots =
            {
                ["node-a"] = Snapshot("node-a", 3, eventTime.AddSeconds(120), "same-event"),
                ["node-b"] = Snapshot("node-b", 5, eventTime, "same-event")
            }
        };
        var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client);

        var result = await service.GetAsync("admin", default);

        Assert.Equal(8, result.Security.TotalInterceptCount);
        Assert.Single(result.RecentEvents);
        Assert.Contains(result.Nodes, x => x.NodeId == "node-a" && x.ClockSkewWarning);
        Assert.All(result.Nodes, x => Assert.True(x.CanOperate));
    }

    [Fact]
    public async Task OfflineNodeRetainsLastGoodSnapshotAsStale()
    {
        var options = Options();
        var client = new DashboardClient { Snapshots = { ["node-a"] = Snapshot("node-a", 1, DateTimeOffset.UtcNow, "event-a") } };
        options.Nodes.Remove("node-b");
        var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client);
        Assert.True(Assert.Single((await service.GetAsync("admin", default)).Nodes).Online);

        client.Failed.Add("node-a");
        var offline = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.False(offline.Online);
        Assert.True(offline.Stale);
        Assert.NotNull(offline.Snapshot);
        Assert.NotNull(offline.LastSeenAt);
    }

    [Fact]
    public void EnforcesGlobalAndPerNodePermissions()
    {
        var options = Options();
        var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), new DashboardClient());

        Assert.True(service.CanOperateGlobally("admin"));
        Assert.False(service.CanOperateGlobally("alice"));
        Assert.True(service.CanOperate("alice", ["node-a"]));
        Assert.False(service.CanOperate("alice", ["node-a", "node-b"]));
    }

    private static DistributionOptions Options() => new()
    {
        Enabled = true,
        Role = "Controller",
        AllowedClockSkewSeconds = 30,
        Nodes = new()
        {
            ["node-a"] = new() { Endpoint = "http://node-a" },
            ["node-b"] = new() { Endpoint = "http://node-b" }
        },
        Permissions = new()
        {
            GlobalOperators = ["admin"],
            NodeOperators = new() { ["node-a"] = ["alice"] }
        }
    };

    private static NodeDashboardSnapshot Snapshot(string nodeId, long count, DateTimeOffset timestamp, string eventId) =>
        new(nodeId, "1.2.3", "config-1", "Idle", timestamp, 60, 2,
            new NodeSecuritySnapshot(count, count, 0, 0, 0, 0, 0, 1), [],
            [new NodeSecurityEvent(eventId, "192.0.2.1", "site", "rule", "waf", count, timestamp)]);

    private sealed class DashboardClient : IDistributionNodeClient
    {
        public Dictionary<string, NodeDashboardSnapshot> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken)
            => Failed.Contains(nodeId) ? Task.FromException<NodeDashboardSnapshot>(new HttpRequestException("offline"))
                : Task.FromResult(Snapshots[nodeId]);
        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
