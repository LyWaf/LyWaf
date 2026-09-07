using LyWaf.Config;
using LyWaf.Policy;
using Microsoft.AspNetCore.Http;
using LyWaf.Services.Metrics;
using Xunit;

namespace LyWaf.Tests;

public sealed class AdvancedLoadBalancingTests
{
    [Fact]
    public void FiltersPriorityThenRegionZoneAndSubset()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Region"] = "east";
        context.Request.Headers["X-Zone"] = "a";
        context.Request.Headers["X-Track"] = "canary";
        Candidate[] candidates =
        [
            new("fallback", Meta(("Priority", "1"))),
            new("stable", Meta(("Priority", "0"), ("Region", "east"), ("Zone", "a"), ("Subset", "stable"))),
            new("canary", Meta(("Priority", "0"), ("Region", "east"), ("Zone", "a"), ("Subset", "canary"))),
            new("west", Meta(("Priority", "0"), ("Region", "west"), ("Zone", "b"), ("Subset", "canary")))
        ];
        var cluster = Meta(("AdvancedLb.RegionHeader", "X-Region"), ("AdvancedLb.ZoneHeader", "X-Zone"),
            ("AdvancedLb.SubsetHeader", "X-Track"), ("AdvancedLb.SubsetKey", "Subset"));

        var selected = AdvancedLoadBalancingPolicy.FilterCandidates(context, candidates, cluster, item => item.Metadata);

        Assert.Equal("canary", Assert.Single(selected).Id);
    }

    [Fact]
    public void StrictSubsetCanRejectWhenNoCandidateMatches()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Track"] = "missing";
        Candidate[] candidates = [new("stable", Meta(("Subset", "stable")))];
        var cluster = Meta(("AdvancedLb.SubsetHeader", "X-Track"), ("AdvancedLb.SubsetFallback", "None"));

        Assert.Empty(AdvancedLoadBalancingPolicy.FilterCandidates(context, candidates, cluster, item => item.Metadata));
    }

    [Fact]
    public void SlowStartRampsWeightToFullValue()
    {
        Assert.Equal(1, AdvancedLoadBalancingPolicy.CalculateEffectiveWeight(20, 100, TimeSpan.Zero));
        Assert.Equal(10, AdvancedLoadBalancingPolicy.CalculateEffectiveWeight(20, 100, TimeSpan.FromSeconds(50)));
        Assert.Equal(20, AdvancedLoadBalancingPolicy.CalculateEffectiveWeight(20, 100, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void LyConfigPreservesDestinationAndClusterMetadata()
    {
        var parsed = LyConfigParser.Parse("""
            example.test {
                proxy {
                    to {
                        primary { Address = "http://10.0.0.1:8080" Priority = 0 Region = east Zone = a Subset = stable Weight = 10 SlowStartSeconds = 30 }
                        failover { Address = "http://10.0.1.1:8080" Priority = 1 Region = west Zone = b Subset = stable }
                    }
                    AdvancedLb {
                        RegionHeader = X-Region
                        ZoneHeader = X-Zone
                        SubsetHeader = X-Track
                        SubsetFallback = None
                        SlowStartSeconds = 60
                    }
                }
            }
            """);
        var app = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var reverseProxy = Assert.IsType<Dictionary<string, object>>(app["ReverseProxy"]);
        var clusters = Assert.IsType<Dictionary<string, object>>(reverseProxy["Clusters"]);
        var cluster = clusters.Values.Select(Assert.IsType<Dictionary<string, object>>)
            .Single(item => item.GetValueOrDefault("LoadBalancingPolicy")?.ToString() == "Advanced");
        Assert.Equal("Advanced", cluster["LoadBalancingPolicy"]);
        var metadata = Assert.IsType<Dictionary<string, object>>(cluster["Metadata"]);
        Assert.Equal("X-Region", metadata["AdvancedLb.RegionHeader"]);
        var destinations = Assert.IsType<Dictionary<string, object>>(cluster["Destinations"]);
        var primary = Assert.IsType<Dictionary<string, object>>(destinations["dest1"]);
        var primaryMetadata = Assert.IsType<Dictionary<string, object>>(primary["Metadata"]);
        Assert.Equal("0", primaryMetadata["Priority"]);
        Assert.Equal("east", primaryMetadata["Region"]);
        Assert.Equal("30", primaryMetadata["SlowStartSeconds"]);
    }

    [Fact]
    public void SuccessfulInFlightRequestDoesNotCancelEjection()
    {
        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        var policy = new OutlierPolicy(true, 1, 1, 10, 1, 50, 30_000, 100);
        var entry = new OutlierDetectionRegistry.Entry("cluster", "destination", "http://upstream", policy, metrics);
        entry.Eject("consecutive_5xx");

        entry.Record(connectionFailure: false, serverError: false);

        Assert.True(entry.IsEjected);
        Assert.Equal("ejected", entry.Snapshot().State);
    }

    private sealed record Candidate(string Id, IReadOnlyDictionary<string, string> Metadata);
    private static Dictionary<string, string> Meta(params (string Key, string Value)[] values) =>
        values.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase);
}
