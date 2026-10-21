using System.Net;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Telemetry;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yarp.ReverseProxy;

namespace LyWaf.Tests;

public sealed class NodeTelemetryTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 19, 12, 1, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActualMiddlewareAgentHttpClientAndControllerCarryRawWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new Clock { Now = NodeTelemetryCollector.Minute(now).AddMinutes(-1) };
        var collector = new NodeTelemetryCollector(clock);
        var secondCollector = new NodeTelemetryCollector(clock);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(collector);
        builder.Services.AddHostedService<NodeCommunicationHttpObserver>();
        builder.Services.AddTransient<IStartupFilter, NodeCommunicationObservationStartup>();
        builder.Services.AddReverseProxy().LoadFromMemory([], []);
        await using var app = builder.Build();
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new()));
        var node = new DistributionNodeService(new StaticOptionsMonitor<DistributionOptions>(new()
        { NodeId = "a", StateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }),
            null!, null!, new ConfigurationBuilder().Build());
        var logA = new CcBanLog(); var logB = new CcBanLog();
        var bansA = new NodeLogicalBanTelemetry(logA, clock); var bansB = new NodeLogicalBanTelemetry(logB, clock);
        var logicalDecision = new CcDecision(new("rule", "revision", "host:example", "192.0.2.1", "policy", CcPolicyGeneration.Initial),
            new string('A', 64), clock.Now, "a", 100, default, clock.Now.AddSeconds(1), clock.Now.AddMinutes(10));
        logA.UpsertDecision(logicalDecision, "rule"); logB.UpsertDecision(logicalDecision with { OriginNode = "b" }, "rule");
        var agent = new NodeDashboardAgent(node, store, app.Services.GetRequiredService<IProxyStateLookup>(), collector,
            diagnostics: new NodeDashboardDiagnostics(), logicalBans: bansA);
        var secondNode = new DistributionNodeService(new StaticOptionsMonitor<DistributionOptions>(new()
        { NodeId = "b", StateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }),
            null!, null!, new ConfigurationBuilder().Build());
        var secondAgent = new NodeDashboardAgent(secondNode, store, app.Services.GetRequiredService<IProxyStateLookup>(), secondCollector,
            diagnostics: new NodeDashboardDiagnostics(), logicalBans: bansB);
        app.Map("/traffic", branch =>
        {
            branch.UseMiddleware<TelemetryMiddleware>();
            branch.Run(context =>
            {
                context.Items["LyWaf.Intercepted"] = true;
                context.Response.StatusCode = 503;
                return Task.CompletedTask;
            });
        });
        app.Map("/healthy", branch =>
        {
            branch.Use(next => new TelemetryMiddleware(next, secondCollector).InvokeAsync);
            branch.Run(context => { context.Response.StatusCode = 200; return Task.CompletedTask; });
        });
        // Exercise the existing client's wire contract without changing/auth-testing node endpoints here.
        app.MapPost("/api/v1/distribution/agent/dashboard", () => agent.Capture());
        app.MapPost("/b/api/v1/distribution/agent/dashboard", () => secondAgent.Capture());
        var incompatible = false;
        app.MapPost("/old/api/v1/distribution/agent/dashboard", () => agent.Capture() with
        {
            NodeId = "old",
            Telemetry = incompatible ? collector.Capture().Select(window => window with
            { BoundsMilliseconds = window.BoundsMilliseconds.Select(bound => bound * 2).ToArray() }).ToArray() : null
        });
        app.MapPost("/missing/api/v1/distribution/agent/dashboard", () => agent.Capture() with { NodeId = "missing", Telemetry = null });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            for (var i = 0; i < 100; i++)
            {
                using var response = await http.GetAsync(address + (i < 10 ? "/traffic" : "/healthy"));
                Assert.Equal(i < 10 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
            }
            clock.Now = now;
            var options = Options("a", "b");
            options.Nodes["a"].Endpoint = address;
            options.Nodes["b"].Endpoint = address + "/b";
            var client = new DistributionNodeClient(new StaticOptionsMonitor<DistributionOptions>(options), new Factory(http));
            await client.GetDashboardAsync("a", default);
            using var controller = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client, clock);
            await controller.RefreshAsync(default);
            var complete = await controller.GetAsync("admin", default);
            var result = complete.Telemetry!;
            Assert.Equal("Complete", result.Status);
            Assert.Equal(100, result.Requests);
            Assert.Equal(10, result.Errors);
            Assert.Equal(10, result.Intercepts);
            Assert.Equal(.1, result.ErrorRate);
            Assert.Equal(.1, result.InterceptRate);
            Assert.NotNull(result.P95Milliseconds);
            Assert.Equal(100, result.Counts.Sum());
            Assert.Equal(1, result.Visitors!.Count);
            Assert.Equal(1, result.LogicalBans!.Count);
            Assert.False(result.LogicalBans.Final);
            Assert.All(complete.Nodes, item => Assert.True(Guid.TryParseExact(item.Snapshot!.BootId, "N", out _)));
            Assert.Contains(complete.Nodes.SelectMany(item => item.Snapshot!.Diagnostics!.Communication.Series), item => item.Requests > 0);
            options.Nodes["old"] = new() { Endpoint = address + "/old", ProtocolVersion = 1 };
            await controller.RefreshAsync(default);
            var partial = await controller.GetAsync("admin", default);
            Assert.Equal("telemetry_missing", partial.Telemetry!.MissingNodes["old"]);
            Assert.Equal("Partial", partial.Telemetry.Status);
            Assert.Equal(100, partial.Telemetry.Requests);
            incompatible = true;
            await controller.RefreshAsync(default);
            var incompatibleDashboard = await controller.GetAsync("admin", default);
            Assert.Equal("incompatible_histogram", incompatibleDashboard.Telemetry!.MissingNodes["old"]);
            incompatible = false;
            options.Nodes.Remove("a");
            options.Nodes.Remove("b");
            options.Nodes.Remove("old");
            options.Nodes["missing"] = new() { Endpoint = address + "/missing", ProtocolVersion = 1 };
            await controller.RefreshAsync(default);
            var missing = await controller.GetAsync("admin", default);
            Assert.Empty(missing.Telemetry!.IncludedNodes);
            // Optional cross-runtime handoff: the UI harness renders actual Controller wire output.
            if (Environment.GetEnvironmentVariable("LYWAF_TELEMETRY_E2E_OUTPUT") is { Length: > 0 } output)
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { complete, partial, incompatible = incompatibleDashboard, missing },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task WeightedRatiosAndMergedQuantilesNotNodeAveragesAndOfflineHistoryRetained()
    {
        var clock = new Clock { Now = End };
        var client = new Client();
        client.Values["a"] = Snapshot("a", Window(100, 100, 100, [100, 0, 0]));
        client.Values["b"] = Snapshot("b", Window(900, 0, 0, [0, 900, 0]));
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(Options("a", "b")), client, clock);
        await service.RefreshAsync(default);
        var result = (await service.GetAsync("admin", default)).Telemetry!;
        Assert.Equal("Complete", result.Status);
        Assert.Equal(.1, result.ErrorRate);
        Assert.Equal(.1, result.InterceptRate);
        Assert.Equal(100d, result.P95Milliseconds);
        Assert.Equal(100d, result.P99Milliseconds);
        Assert.Equal(new long[] { 100, 900, 0 }, result.Counts);
        await service.RefreshAsync(default);
        Assert.Equal(1000, (await service.GetAsync("admin", default)).Telemetry!.Requests);
        client.Offline = true;
        await service.RefreshAsync(default);
        var offline = await service.GetAsync("admin", default);
        Assert.All(offline.Nodes, n => Assert.False(n.Online));
        Assert.Equal(1000, offline.Telemetry!.Requests);
        Assert.Equal("Complete", offline.Telemetry.Status);
        clock.Now = End.AddMinutes(1);
        Assert.Equal("Partial", (await service.GetAsync("admin", default)).Telemetry!.Status);
        Assert.Equal(0, (await service.GetAsync("admin", default)).Telemetry!.Requests);
    }

    [Theory]
    [InlineData("missing", "telemetry_missing")]
    [InlineData("bounds", "incompatible_histogram")]
    [InlineData("schema", "incompatible_histogram")]
    [InlineData("counts", "invalid_histogram_or_counts")]
    [InlineData("negative", "invalid_histogram_or_counts")]
    [InlineData("partial", "window_incomplete")]
    [InlineData("duplicate", "window_missing_or_duplicate")]
    [InlineData("old", "window_missing_or_duplicate")]
    [InlineData("boot", "invalid_histogram_or_counts")]
    [InlineData("watermark", "invalid_watermark")]
    public async Task InvalidOrMissingNodeIsNotSilentlyZero(string kind, string reason)
    {
        var valid = Window(10, 2, 3, [10, 0, 0]);
        var bad = kind switch
        {
            "bounds" => valid with { BoundsMilliseconds = [1, 200] },
            "schema" => valid with { Schema = "other" },
            "counts" => valid with { Counts = [1, 2, 0] },
            "negative" => valid with { Errors = -1 },
            "partial" => valid with { Complete = false },
            "old" => valid with { Start = End.AddMinutes(-2), End = End.AddMinutes(-1) },
            "boot" => valid with { BootId = "" },
            "watermark" => valid with { Watermark = End.AddSeconds(1) },
            _ => valid
        };
        var client = new Client();
        client.Values["a"] = Snapshot("a", valid);
        client.Values["b"] = Snapshot("b", bad) with { Telemetry = kind == "missing" ? null : kind == "duplicate" ? [bad, bad] : [bad] };
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(Options("a", "b")), client, new Clock { Now = End });
        await service.RefreshAsync(default);
        var aggregate = (await service.GetAsync("admin", default)).Telemetry!;
        Assert.Equal("Partial", aggregate.Status);
        Assert.Equal(reason, aggregate.MissingNodes["b"]);
        Assert.Equal(10, aggregate.Requests);
        Assert.Equal(.2, aggregate.ErrorRate);
        Assert.Equal(new[] { "a" }, aggregate.IncludedNodes);
    }

    [Fact]
    public void CollectorStartupEmptyWindowsRetentionAndRestartAreExplicit()
    {
        var clock = new Clock { Now = End.AddSeconds(-30) };
        var collector = new NodeTelemetryCollector(clock);
        collector.Record(5, 200, false);
        clock.Now = End;
        Assert.False(collector.Capture().Single(x => x.End == End).Complete);
        clock.Now = End.AddMinutes(1);
        var empty = collector.Capture().Single(x => x.Start == End);
        Assert.True(empty.Complete);
        Assert.Equal(0, empty.Requests);
        var restarted = new NodeTelemetryCollector(clock);
        Assert.NotEqual(empty.BootId, restarted.Capture()[0].BootId);
        Assert.False(restarted.Capture().Single(x => x.Start == End).Complete);
        for (var i = 0; i < 100; i++)
        { clock.Now = clock.Now.AddMinutes(1); collector.Record(10, 200, false); }
        Assert.Equal(4, collector.Capture().Count);
        Assert.All(collector.Capture(), x => Assert.True(x.Requests <= 1));
    }

    [Fact]
    public async Task ExceptionIsOneFailedCompletionEvenWithoutExporter()
    {
        var clock = new Clock { Now = End.AddMinutes(-1) };
        var collector = new NodeTelemetryCollector(clock);
        var middleware = new TelemetryMiddleware(_ => throw new InvalidOperationException("failure"), collector);
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(new DefaultHttpContext()));
        clock.Now = End;
        var result = collector.Capture().Single(x => x.End == End);
        Assert.Equal(1, result.Requests);
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Counts.Sum());
    }

    [Fact]
    public async Task EmptyAndOverflowQuantilesAreNullAndMembershipVersionChanges()
    {
        var client = new Client();
        client.Values["a"] = Snapshot("a", Window(0, 0, 0, [0, 0, 0]));
        var options = Options("a");
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client, new Clock { Now = End });
        await service.RefreshAsync(default);
        var empty = (await service.GetAsync("admin", default)).Telemetry!;
        Assert.Null(empty.P95Milliseconds);
        Assert.Null(empty.ErrorRate);
        client.Values["a"] = Snapshot("a", Window(10, 0, 0, [0, 0, 10]));
        await service.RefreshAsync(default);
        Assert.Null((await service.GetAsync("admin", default)).Telemetry!.P99Milliseconds);
        options.Nodes["b"] = new() { Endpoint = "http://b" };
        var changed = (await service.GetAsync("admin", default)).Telemetry!;
        Assert.NotEqual(empty.ExpectedSetVersion, changed.ExpectedSetVersion);
        Assert.Equal("Partial", changed.Status);
    }

    [Fact]
    public async Task RestartKeepsObservedCompletedWindowButEndpointChangeDoesNot()
    {
        var client = new Client();
        client.Values["a"] = Snapshot("a", Window(10, 2, 0, [10, 0, 0]));
        var options = Options("a");
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(options), client, new Clock { Now = End });
        await service.RefreshAsync(default);
        client.Values["a"] = Snapshot("a", Window(0, 0, 0, [0, 0, 0]) with { Complete = false });
        await service.RefreshAsync(default);
        Assert.Equal(10, (await service.GetAsync("admin", default)).Telemetry!.Requests);
        options.Nodes["a"].Endpoint = "http://replacement";
        await service.RefreshAsync(default);
        var result = (await service.GetAsync("admin", default)).Telemetry!;
        Assert.Equal("Partial", result.Status);
        Assert.Equal(0, result.Requests);
    }

    [Fact]
    public async Task AggregateOverflowExcludesWholeNodeAndClockSkewSurvivesOffline()
    {
        var client = new Client();
        client.Values["a"] = Snapshot("a", Window(long.MaxValue, 0, 0, [long.MaxValue, 0, 0]));
        client.Values["b"] = Snapshot("b", Window(1, 1, 1, [1, 0, 0]));
        using var service = new MultiNodeDashboardService(new StaticOptionsMonitor<DistributionOptions>(Options("a", "b")), client, new Clock { Now = End });
        await service.RefreshAsync(default);
        var result = (await service.GetAsync("admin", default)).Telemetry!;
        Assert.Equal("count_overflow", result.MissingNodes["b"]);
        Assert.Equal(0, result.Errors);
        Assert.Equal(long.MaxValue, result.Counts.Sum());
        client.Values["b"] = client.Values["b"] with { Timestamp = End.AddHours(1) };
        await service.RefreshAsync(default);
        Assert.Equal("clock_skew", (await service.GetAsync("admin", default)).Telemetry!.MissingNodes["b"]);
        client.Offline = true;
        await service.RefreshAsync(default);
        Assert.Equal("clock_skew", (await service.GetAsync("admin", default)).Telemetry!.MissingNodes["b"]);
    }

    private static NodeTelemetryWindow Window(long requests, long errors, long intercepts, long[] counts) =>
        new(Guid.NewGuid().ToString("N"), NodeTelemetryCollector.Schema, End.AddMinutes(-1), End, End, true,
            requests, errors, intercepts, [10, 100], counts);
    private static NodeDashboardSnapshot Snapshot(string id, NodeTelemetryWindow window) =>
        new(id, "1", null, "Active", End, 100, 0, new(0, 0, 0, 0, 0, 0, 0, 0), [], [], Telemetry: [window]);
    private static DistributionOptions Options(params string[] ids) => new()
    {
        Enabled = true, Role = "Controller", SharedSecret = "telemetry-test-secret",
        Nodes = ids.ToDictionary(id => id, id => new DistributionNodeOptions { Endpoint = "http://" + id, ProtocolVersion = 1 })
    };
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Factory(HttpClient http) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => http; }
    private sealed class Client : IDistributionNodeClient
    {
        public Dictionary<string, NodeDashboardSnapshot> Values = new();
        public bool Offline;
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string id, CancellationToken token) => Offline
            ? Task.FromException<NodeDashboardSnapshot>(new HttpRequestException("offline"))
            : Task.FromResult(JsonSerializer.Deserialize<NodeDashboardSnapshot>(JsonSerializer.Serialize(Values[id]))!);
        public Task<NodeAcknowledgement> PrepareAsync(string id, PrepareConfigurationRequest r, CancellationToken t) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> CommandAsync(string id, string op, DistributionCommandRequest r, CancellationToken t) => throw new NotSupportedException();
        public Task<DistributionNodeStatus> GetStatusAsync(string id, CancellationToken t) => throw new NotSupportedException();
    }
}
