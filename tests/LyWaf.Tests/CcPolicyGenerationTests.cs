using System.Reflection;
using System.Text.Json;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcPolicyGenerationTests
{
    private const string Ip = "192.0.2.139";

    [Theory]
    [InlineData(CcRuleType.FrequentAccess)]
    [InlineData(CcRuleType.FrequentAttack)]
    [InlineData(CcRuleType.FrequentError)]
    public void ReleaseFencesDelayedCountersAndPenaltiesAndAllowsNewTrafficToTrigger(CcRuleType type)
    {
        var rule = Rule(type);
        using var local = Store("a");
        using var remote = Store("b");
        var checker = Checker(local, rule);
        var peer = Checker(remote, rule);
        Assert.False(Request(checker, type).IsTriggered);
        Assert.True(Request(checker, type).IsTriggered);
        var old = local.ExportMutations().ToArray();
        foreach (var mutation in old) remote.Merge(mutation);
        peer.ReleaseRule(Context(), rule, Ip);
        var fence = Assert.Single(remote.ExportMutations(), m => m.Domain == CcPolicyGeneration.Domain);
        local.Merge(fence);

        foreach (var mutation in old.Reverse())
            Assert.False(local.Merge(mutation with { NodeId = "offline", Sequence = 1_000_000,
                ChangedAt = DateTimeOffset.UtcNow.AddDays(1), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }));
        Assert.Empty(local.GetSnapshots("cc.triggered"));
        Assert.False(Request(checker, type).IsTriggered);
        Assert.True(Request(checker, type).IsTriggered);
        Assert.Contains(local.GetSnapshots("cc.triggered"), s => s.Key.StartsWith("ccg:1:", StringComparison.Ordinal));
    }

    [Fact]
    public void NewGenerationDataCanArriveBeforeFenceButRemainsDormant()
    {
        var rule = Rule();
        using var sender = Store("sender");
        using var receiver = Store("receiver");
        var checker = Checker(sender, rule);
        checker.ReleaseRule(Context(), rule, Ip);
        Request(checker);
        Request(checker);
        var mutations = sender.ExportMutations();
        foreach (var mutation in mutations.Where(m => m.Domain != CcPolicyGeneration.Domain)) receiver.Merge(mutation);
        Assert.Empty(receiver.GetSnapshots("cc.triggered"));
        receiver.Merge(Assert.Single(mutations, m => m.Domain == CcPolicyGeneration.Domain));
        Assert.True(Checker(receiver, rule).CheckRequest(Context("a.example", "/other"), Ip).IsTriggered);
    }

    [Fact]
    public void ScopeAndExplicitGroupArePreservedWithoutTouchingGlobalBan()
    {
        var rule = Rule();
        using var store = Store("scope");
        var checker = Checker(store, rule);
        store.Set("ip.blocked", Ip, "manual", TimeSpan.FromHours(1));
        foreach (var host in new[] { "a.example", "b.example" })
        {
            checker.CheckRequest(Context(host), Ip);
            checker.CheckRequest(Context(host), Ip);
        }
        checker.ReleaseRule(Context(), rule, Ip);
        Assert.False(checker.CheckRequest(Context(), Ip).IsTriggered);
        Assert.True(checker.CheckRequest(Context("b.example"), Ip).IsTriggered);
        Assert.True(store.TryGet<string>("ip.blocked", Ip, out var reason));
        Assert.Equal("manual", reason);
        rule.ProtectionGroup = "shared";
        checker.CheckRequest(Context(), Ip);
        Assert.True(checker.CheckRequest(Context("b.example"), Ip).IsTriggered);
        checker.ReleaseRule(Context("b.example"), rule, Ip);
        Assert.False(checker.CheckRequest(Context(), Ip).IsTriggered);
        Assert.True(checker.CheckRequest(Context("b.example"), Ip).IsTriggered);
    }

    [Fact]
    public async Task CaptchaCompletionAndInFlightDecisionCannotAffectNextGeneration()
    {
        var rule = Rule();
        rule.Action = CcAction.Captcha;
        using var store = Store("captcha");
        var checker = Checker(store, rule);
        Request(checker);
        var oldDecision = Request(checker);
        var oldSubject = checker.GetCaptchaSubject(Context(), Ip)!;
        checker.ClearTriggeredRecords(oldSubject);
        Request(checker);
        Assert.True(Request(checker).IsTriggered);
        var subject = checker.GetCaptchaSubject(Context(), Ip);
        Assert.NotEqual(oldSubject, subject);
        checker.ClearTriggeredRecords(oldSubject);
        Assert.Equal(subject, checker.GetCaptchaSubject(Context(), Ip));
        var context = Context();
        await checker.ExecuteAction(context, oldDecision, Ip);
        Assert.False(oldDecision.IsTriggered);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public void ConcurrentReleasesConvergeAndTransportSequenceCannotRollBackGeneration()
    {
        var key = CcRuleStateKey.Create(Rule(), Ip, "host:a.example");
        using var first = Store("a");
        using var second = Store("b");
        first.AdvanceCcPolicyGeneration(key);
        second.AdvanceCcPolicyGeneration(key);
        var a = Assert.Single(first.ExportMutations());
        var b = Assert.Single(second.ExportMutations());
        first.Merge(b);
        second.Merge(a);
        Assert.Equal(first.GetCcPolicyGeneration(key), second.GetCcPolicyGeneration(key));
        var current = first.AdvanceCcPolicyGeneration(key);
        Assert.Equal(2, current.Version);
        Assert.False(first.Merge(CcReleaseTestKeys.Sign(a with { Sequence = 1_000_000 })));
        Assert.False(first.Merge(CcReleaseTestKeys.Sign(b with { Sequence = 2_000_000 })));
        Assert.Equal(current, first.GetCcPolicyGeneration(key));
        Parallel.For(0, 16, _ => first.AdvanceCcPolicyGeneration(key));
        Assert.Equal(18, first.GetCcPolicyGeneration(key).Version);
    }

    [Fact]
    public void ReleaseSurvivesRestartEvenWhenOrdinaryJournalQueueIsFull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-generation-" + Guid.NewGuid().ToString("N"));
        var rule = Rule();
        RuntimeStateMutation[] old;
        CcPolicyGeneration generation;
        using (var store = Store("a", directory))
        {
            var checker = Checker(store, rule);
            Request(checker);
            Request(checker);
            old = store.ExportMutations().ToArray();
            for (var i = 0; i < 150; i++) store.Set("other", i.ToString(), true);
            Assert.True(store.GetStatus().DroppedMutations > 0);
            generation = checker.ReleaseRule(Context(), rule, Ip);
        }
        using var restarted = Store("a", directory);
        var key = CcRuleStateKey.Create(rule, Ip, "host:a.example");
        Assert.Equal(generation, restarted.GetCcPolicyGeneration(key));
        foreach (var mutation in old) Assert.False(restarted.Merge(mutation));
        Assert.False(Request(Checker(restarted, rule)).IsTriggered);
        var fence = Assert.Single(restarted.ExportMutations(), m => m.Domain == CcPolicyGeneration.Domain);
        Assert.Null(fence.ExpiresAt);
    }

    [Fact]
    public void GenerationCannotExpireBeDeletedOrBeReplacedThroughGenericWrites()
    {
        using var store = Store("validate");
        var key = CcRuleStateKey.Create(Rule(), Ip);
        store.AdvanceCcPolicyGeneration(key);
        var fence = Assert.Single(store.ExportMutations());
        Assert.Throws<InvalidOperationException>(() => store.Remove(CcPolicyGeneration.Domain, key));
        Assert.Throws<InvalidOperationException>(() => store.Set(CcPolicyGeneration.Domain, key, CcPolicyGeneration.Initial));
        Assert.Throws<InvalidOperationException>(() => store.TryTake<CcPolicyGeneration>(CcPolicyGeneration.Domain, key, out _));
        Assert.Throws<InvalidOperationException>(() => store.Merge(fence with { Deleted = true }));
        Assert.Throws<InvalidOperationException>(() => store.Merge(fence with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }));
        Assert.Throws<InvalidOperationException>(() => store.Merge(fence with { Value = JsonSerializer.SerializeToElement(-1) }));
    }

    [Fact]
    public void FailedDurableReleaseDoesNotClearPenaltyOrAdvanceGeneration()
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-generation-locked-journal-" + Guid.NewGuid().ToString("N"));
        using var store = Store("journal-lock-test", path);
        var rule = Rule();
        var checker = Checker(store, rule);
        Request(checker);
        Assert.True(Request(checker).IsTriggered);
        using var blockedJournal = new FileStream(Path.Combine(path, "cc-policy-generations.jsonl"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => checker.ReleaseRule(Context(), rule, Ip));
        Assert.True(checker.CheckRequest(Context("a.example", "/other"), Ip).IsTriggered);
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration(CcRuleStateKey.Create(rule, Ip, "host:a.example")));
    }

    [Fact]
    public void ReceivedFenceSurvivesRestartAndCorruptFenceJournalFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cc-generation-peer-" + Guid.NewGuid().ToString("N"));
        var key = CcRuleStateKey.Create(Rule(), Ip);
        using var sender = Store("sender");
        var expected = sender.AdvanceCcPolicyGeneration(key);
        using (var receiver = Store("receiver", directory)) receiver.Merge(Assert.Single(sender.ExportMutations()));
        using (var restarted = Store("receiver", directory)) Assert.Equal(expected, restarted.GetCcPolicyGeneration(key));
        File.AppendAllText(Path.Combine(directory, "cc-policy-generations.jsonl"), "incomplete");
        Assert.Throws<JsonException>(() => Store("receiver", directory));
    }

    private static AdvancedCcRule Rule(CcRuleType type = CcRuleType.FrequentAccess) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Type = type, Threshold = 2, Period = 3600,
        Action = CcAction.Block, Conditions = [new() { Values = ["/api"] }]
    };

    private static CcCheckResult Request(CcRuleChecker checker, CcRuleType type = CcRuleType.FrequentAccess)
    {
        var context = Context();
        if (type == CcRuleType.FrequentAttack) checker.RecordAttackEvent(context, Ip);
        if (type == CcRuleType.FrequentError) checker.RecordErrorEvent(context, Ip, 500);
        return checker.CheckRequest(context, Ip);
    }

    private static CcRuleChecker Checker(IRuntimeStateStore store, AdvancedCcRule rule)
    {
        var provider = DispatchProxy.Create<IStatisticService, Rules>();
        ((Rules)(object)provider).Rule = rule;
        return new CcRuleChecker(provider, null!, store);
    }

    public class Rules : DispatchProxy
    {
        public AdvancedCcRule Rule = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IStatisticService.GetAdvancedCcRules) => new List<AdvancedCcRule> { Rule },
            nameof(IStatisticService.GetAdvancedCcRulesByType) => new List<AdvancedCcRule> { Rule }.Where(r => r.Type == (CcRuleType)args![0]!).ToList(),
            _ => throw new NotSupportedException()
        };
    }

    private static DefaultHttpContext Context(string host = "a.example", string path = "/api")
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static RuntimeStateStore Store(string node, string? directory = null) => new(new StaticOptionsMonitor<RuntimeStateOptions>(CcReleaseTestKeys.Configure(new()
    {
        NodeId = node, QueueCapacity = 100,
        StateDirectory = directory ?? Path.Combine(Path.GetTempPath(), "cc-generation-" + Guid.NewGuid().ToString("N"))
    })));
}
