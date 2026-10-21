using System.Reflection;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcRemoteUnbanTests
{
    [Fact]
    public void RemoteAuthorizedReleaseDoesNotLeaveProcessLocalPenalty()
    {
        var rule = new AdvancedCcRule
        {
            Id = Guid.NewGuid().ToString("N"), Threshold = 1, Action = CcAction.Reject,
            Conditions = [new() { Values = ["/api"] }]
        };
        var statistic = DispatchProxy.Create<IStatisticService, RuleProvider>();
        ((RuleProvider)(object)statistic).Rules = [rule];
        using var local = Store("a");
        using var remote = Store("b");
        var checker = new CcRuleChecker(statistic, null!, local);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("site.example");
        context.Request.Path = "/api";
        Assert.True(checker.CheckRequest(context, "192.0.2.1").IsTriggered);
        foreach (var mutation in local.ExportMutations()) remote.Merge(mutation);
        var key = Assert.Single(remote.GetSnapshots("cc.triggered")).Key;
        remote.AdvanceCcPolicyGeneration(key);
        foreach (var mutation in remote.ExportMutations()) local.Merge(mutation);
        context.Request.Path = "/other";
        Assert.False(checker.CheckRequest(context, "192.0.2.1").IsTriggered);
    }

    private static RuntimeStateStore Store(string node) => new(new StaticOptionsMonitor<RuntimeStateOptions>(CcReleaseTestKeys.Configure(new()
    { NodeId = node, StateDirectory = Path.Combine(Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N")) })));

    public class RuleProvider : DispatchProxy
    {
        public List<AdvancedCcRule> Rules { get; set; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IStatisticService.GetAdvancedCcRules) ? Rules : throw new NotSupportedException();
    }
}
