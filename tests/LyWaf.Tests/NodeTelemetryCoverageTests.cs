using LyWaf.Services.Distribution;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeTelemetryCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void VisitorSetsMergeAcrossNodesNormalizeMappedAddressesAndStayWindowScoped()
    {
        var clock = new Clock { Now = Start };
        var a = new NodeTelemetryCollector(clock);
        var b = new NodeTelemetryCollector(clock);
        a.Record(10, 200, false, clientIp: "::ffff:192.0.2.1");
        b.Record(20, 200, false, clientIp: "192.0.2.1");
        b.Record(30, 200, false, clientIp: "2001:db8::1");
        clock.Now = Start.AddMinutes(1);
        var result = NodeTelemetryAggregation.Aggregate([Node("a", a.Capture()), Node("b", b.Capture())], clock.Now);
        Assert.Equal(3, result.Requests);
        Assert.Equal(2, result.Visitors!.Count);
        Assert.Equal("Complete", result.Visitors.Status);
        Assert.DoesNotContain("192.0.2.1", System.Text.Json.JsonSerializer.Serialize(a.Capture()));
        Assert.Null(NodeTelemetryAggregation.Aggregate([Node("a", null)], clock.Now).Visitors!.Count);
    }

    [Fact]
    public void VisitorOverflowIsPartialWithoutDroppingRequestHistogram()
    {
        var clock = new Clock { Now = Start };
        var collector = new NodeTelemetryCollector(clock);
        for (var i = 0; i < 5000; i++) collector.Record(2, 200, false, clientIp: $"10.{i / 65536}.{i / 256 % 256}.{i % 256}");
        clock.Now = Start.AddMinutes(1);
        var windows = collector.Capture();
        Assert.Equal(4096, windows.Single(x => x.Start == Start).VisitorHashes!.Count);
        var aggregate = NodeTelemetryAggregation.Aggregate([Node("a", windows)], clock.Now);
        Assert.Equal("Complete", aggregate.Status);
        Assert.Equal(5000, aggregate.Requests);
        Assert.Equal("Partial", aggregate.Visitors!.Status);
        Assert.Null(aggregate.Visitors.Count);
        Assert.Equal("visitor_set_incomplete", aggregate.Visitors.MissingNodes["a"]);
    }

    [Fact]
    public void ActualBoundedBanLogDeduplicatesLogicalIdsAndSeparatesRequestHits()
    {
        var clock = new Clock { Now = Start };
        var logA = new CcBanLog(); var logB = new CcBanLog();
        var a = new NodeLogicalBanTelemetry(logA, clock); var b = new NodeLogicalBanTelemetry(logB, clock);
        var decision = Decision(1);
        logA.UpsertDecision(decision, "rule"); logA.UpsertDecision(decision with { InterceptedRequests = 99 }, "rule");
        logB.UpsertDecision(decision with { OriginNode = "b", InterceptedRequests = 50 }, "rule");
        clock.Now = Start.AddMinutes(1);
        var result = NodeLogicalBanTelemetry.Aggregate([Node("a", null, a.Capture(Guid.NewGuid().ToString("N"))),
            Node("b", null, b.Capture(Guid.NewGuid().ToString("N")))], Start, clock.Now);
        Assert.Equal(1, result.Count);
        Assert.Equal("Complete", result.Status);
        Assert.False(result.Final);
    }

    [Fact]
    public void BanLogEvictionAndRestartNeverClaimCompleteWindow()
    {
        var clock = new Clock { Now = Start };
        var log = new CcBanLog(); var source = new NodeLogicalBanTelemetry(log, clock);
        for (var i = 0; i < 1001; i++) log.UpsertDecision(Decision(i), "rule");
        clock.Now = Start.AddMinutes(1);
        var snapshot = source.Capture(Guid.NewGuid().ToString("N"));
        var result = NodeLogicalBanTelemetry.Aggregate([Node("a", null, snapshot)], Start, clock.Now);
        Assert.Null(result.Count);
        Assert.Equal("log_retention_gap", result.MissingNodes["a"]);
        var restarted = new NodeLogicalBanTelemetry(log, clock);
        Assert.False(restarted.Capture(Guid.NewGuid().ToString("N")).Single(x => x.Start == Start).Complete);
    }

    [Fact]
    public void RetainedLogicalWindowKeepsOriginalGenerationAndLateDecisionsRemainProvisional()
    {
        var clock = new Clock { Now = Start };
        var log = new CcBanLog(); var source = new NodeLogicalBanTelemetry(log, clock);
        var boot = Guid.NewGuid().ToString("N");
        log.UpsertDecision(Decision(1), "rule");
        clock.Now = Start.AddMinutes(1);
        var previous = Node("a", null, source.Capture(boot)).Snapshot!;
        log.UpsertDecision(Decision(2), "late");
        var late = Node("a", null, source.Capture(boot));
        var updated = NodeLogicalBanTelemetry.Aggregate([late], Start, clock.Now);
        Assert.Equal(2, updated.Count);
        Assert.False(updated.Final);
        var restarted = new NodeLogicalBanTelemetry(new CcBanLog(), clock);
        var incoming = Node("a", null, restarted.Capture(Guid.NewGuid().ToString("N"))).Snapshot!;
        var retained = NodeLogicalBanTelemetry.Retain(incoming, previous, clock.Now);
        var result = NodeLogicalBanTelemetry.Aggregate([Node("a", null, retained)], Start, clock.Now);
        Assert.Equal(1, result.Count);
        Assert.Equal(boot, result.SourceGenerations!["a"]);
        var incompatible = retained!.Select(w => w with { Method = "future-v2" }).ToArray();
        var partial = NodeLogicalBanTelemetry.Aggregate([Node("a", null, incompatible)], Start, clock.Now);
        Assert.Null(partial.Count);
        Assert.Equal("incompatible_method", partial.MissingNodes["a"]);
    }

    internal static CcDecision Decision(int id) => new(new("rule", "revision", "host:example", "192.0.2.1", "policy", CcPolicyGeneration.Initial),
        id.ToString("X64"), Start, "a", 100, default, Start.AddSeconds(1), Start.AddMinutes(10));
    private static ManagedNodeSnapshot Node(string id, IReadOnlyList<NodeTelemetryWindow>? windows, IReadOnlyList<NodeLogicalBanWindow>? bans = null) =>
        new(id, "https://" + id, [], false, true, null, false, Start.AddMinutes(1), null, false,
            new(id, "1", null, "Active", Start.AddMinutes(1), 120, 0, new(0, 0, 0, 0, 0, 0, 0, 0), [], [], Telemetry: windows, LogicalBans: bans));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
}
