using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcConditionGroupTests
{
    [Theory]
    [InlineData("And", "And", false)]
    [InlineData("And", "Or", true)]
    [InlineData("Or", "And", true)]
    [InlineData("Or", "Or", true)]
    public void EvaluatesGroupAndConditionRelationships(string outer, string inner, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api";
        var yes = new CcCondition { Target = CcMatchTarget.UrlPath, Operator = CcMatchOperator.Equal, Values = ["/api"] };
        var no = new CcCondition { Target = CcMatchTarget.UrlPath, Operator = CcMatchOperator.Equal, Values = ["/other"] };
        var rule = new AdvancedCcRule { GroupOperator = outer, ConditionGroups = [
            new() { Operator = inner, Conditions = [yes, no] }, new() { Conditions = [yes] }] };
        Assert.Equal(expected, CcRuleChecker.MatchRuleConditions(context, rule, "127.0.0.1"));
        var json = System.Text.Json.JsonSerializer.Serialize(rule);
        Assert.Equal(expected, CcRuleChecker.MatchRuleConditions(context,
            System.Text.Json.JsonSerializer.Deserialize<AdvancedCcRule>(json)!, "127.0.0.1"));
    }

    [Fact]
    public void EmptyGroupsDoNotMatchButLegacyEmptyRuleRetainsBehavior()
    {
        var context = new DefaultHttpContext();
        Assert.False(CcRuleChecker.MatchRuleConditions(context, new() { ConditionGroups = [] }, "127.0.0.1"));
        Assert.False(CcRuleChecker.MatchRuleConditions(context, new() { ConditionGroups = [new()] }, "127.0.0.1"));
        Assert.True(CcRuleChecker.MatchRuleConditions(context, new(), "127.0.0.1"));
    }
}
