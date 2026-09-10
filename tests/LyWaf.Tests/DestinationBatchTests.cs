using LyWaf.Control;
using Xunit;

namespace LyWaf.Tests;

public sealed class DestinationBatchTests
{
    [Fact]
    public void MixedBatchPreservesIndividualFailuresAndRemovesValidTargets()
    {
        var destinations = new Dictionary<string, object> { ["first"] = new(), ["protected"] = new(), ["last"] = new() };
        var results = ControlApi.ApplyDestinationRemovals(["first", "bad:key", "protected", "missing", "last", "first"],
            destinations, id => id != "protected");
        Assert.Equal(5, results.Count);
        Assert.True(results[0].Success);
        Assert.Equal("invalid_id", results[1].Code);
        Assert.Equal("not_patch_destination", results[2].Code);
        Assert.Equal("not_found", results[3].Code);
        Assert.True(results[4].Success);
        Assert.Equal("protected", Assert.Single(destinations).Key);
    }

    [Fact]
    public void InvalidIdsNeverReachConfigurationLookup()
    {
        var results = ControlApi.ApplyDestinationRemovals(["", " ", ":", new string('x', 201)], [],
            _ => throw new InvalidOperationException("Lookup must not run"));
        Assert.All(results, item => Assert.Equal("invalid_id", item.Code));
    }
}
