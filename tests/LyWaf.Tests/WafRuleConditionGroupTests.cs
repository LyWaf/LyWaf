using LyWaf.Services.WafRule;
using LyWaf.Services.Protect;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public class WafRuleConditionGroupTests
{
    [Theory]
    [InlineData(WafLogicalOperator.And, false)]
    [InlineData(WafLogicalOperator.Or, true)]
    public void MatchRule_AppliesOperatorInsideGroup(WafLogicalOperator groupOperator, bool expected)
    {
        var context = CreateContext("/admin", "GET");
        var rule = Rule(WafLogicalOperator.And,
            Group(groupOperator, Path("/admin"), Method("POST")));

        Assert.Equal(expected, WafRuleService.MatchRule(context, rule, "127.0.0.1"));
    }

    [Theory]
    [InlineData(WafLogicalOperator.And, false)]
    [InlineData(WafLogicalOperator.Or, true)]
    public void MatchRule_AppliesOperatorBetweenGroups(WafLogicalOperator groupOperator, bool expected)
    {
        var context = CreateContext("/admin", "GET");
        var rule = Rule(groupOperator,
            Group(WafLogicalOperator.And, Path("/admin")),
            Group(WafLogicalOperator.And, Method("POST")));

        Assert.Equal(expected, WafRuleService.MatchRule(context, rule, "127.0.0.1"));
    }

    [Fact]
    public void HasValidConditions_EnforcesGroupAndConditionLimits()
    {
        Assert.False(WafRuleService.HasValidConditions(Rule(WafLogicalOperator.Or)));
        Assert.False(WafRuleService.HasValidConditions(Rule(WafLogicalOperator.Or,
            Group(WafLogicalOperator.And))));

        var tooManyGroups = Enumerable.Range(0, 6)
            .Select(_ => Group(WafLogicalOperator.And, Path("/")))
            .ToArray();
        Assert.False(WafRuleService.HasValidConditions(Rule(WafLogicalOperator.Or, tooManyGroups)));

        var tooManyConditions = Group(WafLogicalOperator.And,
            Enumerable.Range(0, 21).Select(_ => Path("/")).ToArray());
        Assert.False(WafRuleService.HasValidConditions(Rule(WafLogicalOperator.Or, tooManyConditions)));
    }

    [Fact]
    public void MatchRule_MatchesNormalizedGetInjectionCandidate()
    {
        var context = CreateContext("/search", "GET");
        context.Request.QueryString = new QueryString("?q=%27%20OR%201%3D1");
        var snapshot = Snapshot(query: ["' OR 1=1"]);
        var rule = SystemWafRules.GetRules().Single(rule => rule.Id == "sys-get-injection");

        Assert.True(WafRuleService.MatchRule(context, rule, "127.0.0.1", snapshot));
    }

    [Fact]
    public void MatchRule_MatchesPostBodyInjectionCandidate()
    {
        var context = CreateContext("/login", "POST");
        var snapshot = Snapshot(body: ["1 UNION SELECT password FROM users"]);
        var rule = SystemWafRules.GetRules().Single(rule => rule.Id == "sys-post-injection");

        Assert.True(WafRuleService.MatchRule(context, rule, "127.0.0.1", snapshot));
    }

    private static DefaultHttpContext CreateContext(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        return context;
    }

    private static WafCustomRule Rule(WafLogicalOperator op, params WafConditionGroup[] groups) => new()
    {
        Name = "test",
        GroupOperator = op,
        ConditionGroups = [.. groups]
    };

    private static WafConditionGroup Group(WafLogicalOperator op, params WafCondition[] conditions) => new()
    {
        Operator = op,
        Conditions = [.. conditions]
    };

    private static WafCondition Path(string value) => new()
    {
        Field = WafMatchField.UriPath,
        Operator = WafMatchOperator.Equal,
        Value = value
    };

    private static WafCondition Method(string value) => new()
    {
        Field = WafMatchField.Method,
        Operator = WafMatchOperator.Equal,
        Value = value
    };

    private static RequestInspectionSnapshot Snapshot(
        IReadOnlyList<string>? query = null,
        IReadOnlyList<string>? body = null) =>
        new(query ?? [], [], body ?? [], false, []);
}
