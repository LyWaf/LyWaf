using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class MultiNodeDashboardTests
{
    [Fact]
    public async Task LifecycleUsesFailureThresholdTimeoutAndRecoversOnSuccess()
    {
        var options = Options();
        options.Nodes.Remove("node-b");
        var clock = new TestClock();
        var client = new DashboardClient();
        client.Snapshots["node-a"] = Snapshot("node-a", 1, clock.GetUtcNow(), "event");
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client, clock);
        Assert.Equal("Connecting", Assert.Single((await service.GetAsync("admin", default)).Nodes).Lifecycle);
        await service.RefreshAsync(default);
        client.Failed.Add("node-a");
        for (var failure = 1; failure <= 3; failure++)
        {
            clock.Now = clock.Now.AddSeconds(10);
            await service.RefreshAsync(default);
            var node = Assert.Single((await service.GetAsync("admin", default)).Nodes);
            Assert.Equal(failure < 3 ? "Active" : "Suspect", node.Lifecycle);
            Assert.Equal(failure, node.ConsecutiveFailures);
            Assert.False(node.Online);
        }
        clock.Now = clock.Now.AddSeconds(30);
        Assert.Equal("Offline", Assert.Single((await service.GetAsync("admin", default)).Nodes).Lifecycle);
        client.Failed.Clear();
        await service.RefreshAsync(default);
        var recovered = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal("Active", recovered.Lifecycle);
        Assert.True(recovered.Online);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task AuthenticationFailureIsNotDisguisedAsOffline()
    {
        var options = Options();
        options.Nodes.Remove("node-b");
        var clock = new TestClock();
        var client = new DashboardClient { Handler = (_, _) => Task.FromException<NodeDashboardSnapshot>(
            new HttpRequestException("rejected", null, System.Net.HttpStatusCode.Forbidden)) };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client, clock);
        await service.RefreshAsync(default);
        clock.Now = clock.Now.AddMinutes(5);
        var node = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal("AuthenticationFailed", node.Lifecycle);
        Assert.False(node.Online);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RemoteErrorMetadataIsPreservedWithoutExposingExceptionText()
    {
        var error = new HttpRequestException("private server details");
        error.Data["ErrorCode"] = "node_operation_forbidden";
        error.Data["RemoteTraceId"] = "trace-123";
        var client = new DashboardClient { Handler = (_, _) => Task.FromException<NodeDashboardSnapshot>(error) };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(Options()), client);
        await service.RefreshAsync(default);
        var dashboard = await service.GetAsync("admin", default);
        Assert.All(dashboard.Nodes, node =>
        {
            Assert.False(node.Online);
            Assert.Equal("node_operation_forbidden", node.ErrorCode);
            Assert.Equal("trace-123", node.RemoteTraceId);
            Assert.DoesNotContain("private", node.Error);
        });
    }

    [Fact]
    public async Task ShutdownCancellationDoesNotReplaceLastSuccessfulObservation()
    {
        var options = Options();
        options.Nodes.Remove("node-b");
        var client = new DashboardClient();
        client.Snapshots["node-a"] = Snapshot("node-a", 1, DateTimeOffset.UtcNow, "a");
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client);
        await service.RefreshAsync(default);
        using var cancellation = new CancellationTokenSource();
        client.Handler = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<NodeDashboardSnapshot>(token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cancellation.Token));
        var dashboard = await service.GetAsync("admin", default);
        Assert.True(Assert.Single(dashboard.Nodes).Online);
        Assert.Null(dashboard.Nodes[0].ErrorCode);
    }

    [Fact]
    public async Task BackgroundCollectionRunsWithoutPageRequestsAndReadsDoNotPollNodes()
    {
        var options = Options();
        options.Nodes.Remove("node-b");
        var client = new DashboardClient { Snapshots = { ["node-a"] = Snapshot("node-a", 1, DateTimeOffset.UtcNow, "event") } };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client);
        Assert.False(Assert.Single((await service.GetAsync("admin", default)).Nodes).Online);
        Assert.Equal(0, client.Requests);
        await service.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Assert.Single((await service.GetAsync("admin", default)).Nodes).Online)
                await Task.Delay(10, timeout.Token);
            var count = client.Requests;
            for (var i = 0; i < 10; i++) await service.GetAsync("alice", default);
            Assert.Equal(count, client.Requests);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task WrongNodeIdentityIsNotCachedAsHealthy()
    {
        var options = Options();
        options.Nodes.Remove("node-b");
        var client = new DashboardClient { Snapshots = { ["node-a"] = Snapshot("impostor", 99, DateTimeOffset.UtcNow, "event") } };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client);
        await service.RefreshAsync(default);
        var result = await service.GetAsync("admin", default);
        Assert.False(Assert.Single(result.Nodes).Online);
        Assert.Null(Assert.Single(result.Nodes).Snapshot);
        Assert.Equal(0, result.Security.TotalInterceptCount);
    }

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

        await service.RefreshAsync(default);
        var result = await service.GetAsync("admin", default);

        Assert.Equal(8, result.Security.TotalInterceptCount);
        Assert.NotNull(result.Coverage);
        Assert.True(result.Coverage.Complete);
        Assert.Equal(2, result.Coverage.IncludedNodes);
        Assert.Equal(eventTime, result.Coverage.OldestSnapshotAt);
        Assert.False(result.Coverage.StatisticsPeriodsKnown);
        Assert.Equal(1, result.Security.UniqueAttackIps);
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
        await service.RefreshAsync(default);
        Assert.True(Assert.Single((await service.GetAsync("admin", default)).Nodes).Online);

        client.Failed.Add("node-a");
        await service.RefreshAsync(default);
        var offline = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.False(offline.Online);
        Assert.True(offline.Stale);
        Assert.NotNull(offline.Snapshot);
        Assert.NotNull(offline.LastSeenAt);
        Assert.Null((await service.GetAsync("admin", default)).Security.UniqueAttackIps);
        var coverage = (await service.GetAsync("admin", default)).Coverage!;
        Assert.False(coverage.Complete);
        Assert.Equal(0, coverage.IncludedNodes);
        Assert.Equal(new[] { "node-a" }, coverage.MissingNodes);
        Assert.Null(coverage.OldestSnapshotAt);
    }

    [Fact]
    public async Task MissingIpSetDoesNotProduceMisleadingGlobalDistinctCount()
    {
        var client = new DashboardClient();
        client.Snapshots["node-a"] = Snapshot("node-a", 3, DateTimeOffset.UtcNow, "a");
        client.Snapshots["node-b"] = Snapshot("node-b", 4, DateTimeOffset.UtcNow, "b") with { AttackIps = null };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(Options()), client);
        await service.RefreshAsync(default);
        var dashboard = await service.GetAsync("admin", default);
        Assert.Null(dashboard.Security.UniqueAttackIps);
        Assert.Equal(7, dashboard.Security.TotalInterceptCount);
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

    [Theory]
    [InlineData("Active", true)]
    [InlineData("PendingApproval", false)]
    [InlineData("Draining", false)]
    [InlineData("Revoked", false)]
    [InlineData("bad-config", false)]
    public async Task AdmissionIsSeparateFromSuccessfulObservation(string state, bool healthy)
    {
        var clock = new TestClock();
        var options = Options();
        options.Nodes.Remove("node-b");
        var client = new DashboardClient();
        client.Snapshots["node-a"] = Snapshot("node-a", 2, clock.GetUtcNow(), "event");
        var config = new NodeCommunicationOptions { Enabled = true };
        config.Peers["node-a"] = new() { AdmissionState = state };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client,
            clock, new StaticOptionsMonitor<NodeCommunicationOptions>(config));
        await service.RefreshAsync(default);
        var dashboard = await service.GetAsync("admin", default);
        var node = Assert.Single(dashboard.Nodes);
        Assert.True(node.Online);
        Assert.Equal("Active", node.Lifecycle);
        Assert.Null(node.ErrorCode);
        Assert.Equal(state == "bad-config" ? "Invalid" : state, node.AdmissionState);
        Assert.Equal(healthy, node.HealthyActive);
        Assert.Equal(healthy ? 1 : 0, dashboard.HealthyActiveNodes);
        config.Peers["node-a"].AdmissionState = "Revoked";
        Assert.Equal(0, (await service.GetAsync("admin", default)).HealthyActiveNodes);
        Assert.Equal(1, client.Requests);
    }

    [Fact]
    public async Task AdmissionDoesNotRewriteObservationFailureAndMissingPeersFailClosed()
    {
        var clock = new TestClock();
        var options = Options();
        options.Nodes.Remove("node-b");
        var client = new DashboardClient { Handler = (_, _) => Task.FromException<NodeDashboardSnapshot>(
            new HttpRequestException("denied", null, System.Net.HttpStatusCode.Forbidden)) };
        var config = new NodeCommunicationOptions { Enabled = true };
        config.Peers["node-a"] = new() { AdmissionState = "PendingApproval" };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client,
            clock, new StaticOptionsMonitor<NodeCommunicationOptions>(config));
        await service.RefreshAsync(default);
        var before = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal("AuthenticationFailed", before.Lifecycle);
        Assert.False(before.HealthyActive);
        Assert.NotNull(before.ErrorCode);
        config.Peers["node-a"].AdmissionState = "Revoked";
        var after = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal(before.ErrorCode, after.ErrorCode);
        Assert.Equal(before.Error, after.Error);
        Assert.Equal(before.Lifecycle, after.Lifecycle);
        Assert.Equal(before.ConsecutiveFailures, after.ConsecutiveFailures);
        config.Peers.Clear();
        var unknown = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal("Unknown", unknown.AdmissionState);
        Assert.False(unknown.AdmissionEnabled);
        Assert.False(unknown.HealthyActive);
    }

    [Fact]
    public async Task DisabledPeerIsNotHealthyAndLegacyRemainsExplicit()
    {
        var clock = new TestClock();
        var options = Options();
        options.Nodes.Remove("node-b");
        var client = new DashboardClient();
        client.Snapshots["node-a"] = Snapshot("node-a", 1, clock.GetUtcNow(), "event");
        var config = new NodeCommunicationOptions { Enabled = true };
        config.Peers["node-a"] = new() { Enabled = false, AdmissionState = "Active" };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client,
            clock, new StaticOptionsMonitor<NodeCommunicationOptions>(config));
        await service.RefreshAsync(default);
        var node = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal("Active", node.AdmissionState);
        Assert.False(node.AdmissionEnabled);
        Assert.False(node.HealthyActive);
        config.Enabled = false;
        var legacy = Assert.Single((await service.GetAsync("admin", default)).Nodes);
        Assert.Equal("Legacy", legacy.AdmissionState);
        Assert.True(legacy.HealthyActive);
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
            [new NodeSecurityEvent(eventId, "192.0.2.1", "site", "rule", "waf", count, timestamp)], ["192.0.2.1"]);

    private sealed class DashboardClient : IDistributionNodeClient
    {
        public int Requests;
        public Func<string, CancellationToken, Task<NodeDashboardSnapshot>>? Handler;
        public Dictionary<string, NodeDashboardSnapshot> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (Handler is not null) return Handler(nodeId, cancellationToken);
            return Failed.Contains(nodeId) ? Task.FromException<NodeDashboardSnapshot>(new HttpRequestException("offline"))
                : Task.FromResult(Snapshots[nodeId]);
        }
        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
