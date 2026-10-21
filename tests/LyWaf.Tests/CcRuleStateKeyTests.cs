using System.Text.Json;
using LyWaf.Services.Statistic;
using Xunit;
using Microsoft.AspNetCore.Http;

namespace LyWaf.Tests;

public sealed class CcRuleStateKeyTests
{
    [Fact]
    public void ExplicitProtectionGroupSharesCountersAcrossSitesButNotGroupsOrVersions()
    {
        var rule = new AdvancedCcRule { Id = "rule", ProtectionGroup = " checkout " };
        var first = CcRuleStateKey.Create(rule, "192.0.2.1", "route:a");
        Assert.Equal(first, CcRuleStateKey.Create(rule, "192.0.2.1", "route:b"));
        rule.ProtectionGroup = "different";
        Assert.NotEqual(first, CcRuleStateKey.Create(rule, "192.0.2.1", "route:a"));
        rule.ProtectionGroup = "checkout";
        rule.Threshold++;
        Assert.NotEqual(first, CcRuleStateKey.Create(rule, "192.0.2.1", "route:a"));
        rule.ProtectionGroup = "";
        Assert.NotEqual(CcRuleStateKey.Create(rule, "192.0.2.1", "route:a"),
            CcRuleStateKey.Create(rule, "192.0.2.1", "route:b"));
    }

    [Fact]
    public void DifferentSitesDoNotShareCounterKeysForSameRuleAndIp()
    {
        var rule = new AdvancedCcRule { Id = "rule" };
        var first = new DefaultHttpContext();
        first.Request.Host = new HostString("A.EXAMPLE.");
        var equivalent = new DefaultHttpContext();
        equivalent.Request.Host = new HostString("a.example");
        var other = new DefaultHttpContext();
        other.Request.Host = new HostString("b.example");
        Assert.Equal(CcRuleStateKey.Scope(first), CcRuleStateKey.Scope(equivalent));
        Assert.NotEqual(CcRuleStateKey.Create(rule, "127.0.0.1", CcRuleStateKey.Scope(first)),
            CcRuleStateKey.Create(rule, "127.0.0.1", CcRuleStateKey.Scope(other)));
        first.Request.Host = new HostString("rewritten.example");
        Assert.Equal("host:a.example", CcRuleStateKey.Scope(first));
    }

    [Fact]
    public void EquivalentRulesAcrossNodesHaveSameRevisionDespitePresentationMetadata()
    {
        var rule = new AdvancedCcRule { Id = "rule", Conditions = [new() { Values = ["/api"] }] };
        var copy = JsonSerializer.Deserialize<AdvancedCcRule>(JsonSerializer.Serialize(rule))!;
        copy.Name = "Another display name";
        copy.CreatedAt = rule.CreatedAt.AddDays(1);
        copy.Priority++;
        Assert.Equal(CcRuleStateKey.Create(rule, "127.0.0.1"), CcRuleStateKey.Create(copy, "127.0.0.1"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("period")]
    [InlineData("threshold")]
    [InlineData("action")]
    [InlineData("duration")]
    [InlineData("condition")]
    [InlineData("groups")]
    public void ExecutionChangesProduceDifferentStateKeys(string change)
    {
        var rule = new AdvancedCcRule { Id = "rule" };
        var before = CcRuleStateKey.Create(rule, "127.0.0.1");
        switch (change)
        {
            case "id": rule.Id = "other"; break;
            case "type": rule.Type = CcRuleType.FrequentError; break;
            case "period": rule.Period++; break;
            case "threshold": rule.Threshold++; break;
            case "action": rule.Action = CcAction.Block; break;
            case "duration": rule.ActionSeconds++; break;
            case "condition": rule.Conditions.Add(new() { Values = ["/api"] }); break;
            case "groups": rule.ConditionGroups = []; break;
        }
        Assert.NotEqual(before, CcRuleStateKey.Create(rule, "127.0.0.1"));
    }

    [Fact]
    public void IgnoredLegacyConditionsDoNotChangeGroupedRuleRevision()
    {
        var rule = new AdvancedCcRule { Id = "rule", ConditionGroups = [new() { Conditions = [new() { Values = ["/api"] }] }] };
        var before = CcRuleStateKey.Create(rule, "::1");
        rule.Conditions.Add(new() { Values = ["ignored"] });
        Assert.Equal(before, CcRuleStateKey.Create(rule, "::1"));
        Assert.EndsWith(":::1", before);
        rule.GroupOperator = "And";
        Assert.NotEqual(before, CcRuleStateKey.Create(rule, "::1"));
        Assert.NotEqual(before, CcRuleStateKey.Create(rule, "::2"));
    }
}
