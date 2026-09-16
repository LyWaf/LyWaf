using System.Reflection;
using LyWaf.Services.Protect;
using LyWaf.Services.WafRule;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class WafObservationTests
{
    [Theory]
    [InlineData("/.git/config", WafRuleAction.Reject)]
    [InlineData("/.env", WafRuleAction.Reject)]
    [InlineData("/products", WafRuleAction.Observe)]
    public async Task ObserveDoesNotBypassSubsequentSystemProtection(string path, WafRuleAction expected)
    {
        var service = new WafRuleService(null!, new ConfigurationBuilder().Build(), null!,
            new RequestInspectionService(new StaticOptionsMonitor<ProtectOptions>(new())));
        var observe = new WafCustomRule
        {
            Id = "observe-all", Name = "Observe all", Enabled = true, Source = WafRuleSource.System,
            Action = WafRuleAction.Observe,
            ConditionGroups = [new WafConditionGroup { Conditions =
                [new WafCondition { Field = WafMatchField.UriPath, Operator = WafMatchOperator.Contains, Value = "/" }] }]
        };
        typeof(WafRuleService).GetField("_mergedRules", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new List<WafCustomRule> { observe,
                SystemWafRules.GetRules().Single(rule => rule.Id == "sys-sensitive-files") });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var result = await service.CheckRequest(context, "127.0.0.1");
        Assert.True(result.IsTriggered);
        Assert.Equal(expected, result.Action);
        Assert.Equal(expected == WafRuleAction.Reject ? "sys-sensitive-files" : "observe-all", result.TriggeredRule!.Id);
        Assert.Equal(result.TriggeredRule.Id, context.Items["LyWaf.RuleId"]);
    }
}
