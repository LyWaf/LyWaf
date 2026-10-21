using System.Net;
using System.Reflection;
using LyWaf.Middleware;
using LyWaf.Services.Captcha;
using LyWaf.Services.Metrics;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcScopeExecutionTests
{
    [Theory]
    [InlineData(CcAction.Block, "", false)]
    [InlineData(CcAction.Captcha, "", false)]
    [InlineData(CcAction.Block, "shared", true)]
    [InlineData(CcAction.Captcha, "shared", true)]
    public async Task ReplicatedPenaltyUsesScopeWithoutGlobalIpState(CcAction action, string group, bool shared)
    {
        var ip = "192.0.2.87";
        var rule = new AdvancedCcRule { Id = Guid.NewGuid().ToString("N"), Threshold = 1,
            Action = action, ProtectionGroup = group, Conditions = [new() { Values = ["/api"] }] };
        var provider = DispatchProxy.Create<IStatisticService, CcRemoteUnbanTests.RuleProvider>();
        ((CcRemoteUnbanTests.RuleProvider)(object)provider).Rules = [rule];
        using var first = Store("a");
        using var second = Store("b");
        var checker = new CcRuleChecker(provider, null!, first);
        var remote = new CcRuleChecker(provider, null!, second);
        var request = Context("a.example", "/api");
        var result = checker.CheckRequest(request, ip);
        Assert.True(result.IsTriggered);
        await checker.ExecuteAction(request, result, ip);
        Assert.False(SharedData.ClientFb.TryGetValue(ip, out _));
        Assert.False(SharedData.CaptchaPending.TryGetValue(ip, out _));
        Assert.Empty(first.GetSnapshots("ip.blocked"));
        Assert.Empty(first.GetSnapshots("captcha.pending"));
        foreach (var mutation in first.ExportMutations()) second.Merge(mutation);
        Assert.True(remote.CheckRequest(Context("a.example", "/other"), ip).IsTriggered);
        Assert.Equal(shared, remote.CheckRequest(Context("b.example", "/other"), ip).IsTriggered);

        if (action == CcAction.Captcha)
        {
            var subject = remote.GetCaptchaSubject(Context("a.example", "/other"), ip);
            Assert.NotNull(subject);
            Assert.Equal(shared, remote.GetCaptchaSubject(Context("b.example", "/other"), ip) != null);
            remote.ClearTriggeredRecords(subject!);
            Assert.Null(remote.GetCaptchaSubject(Context("a.example", "/other"), ip));
            Assert.DoesNotContain(second.GetSnapshots("cc.access"), s => !s.Deleted);
        }
        var key = CcRuleStateKey.Create(rule, ip, "host:a.example");
        remote.ReleaseRule(Context("a.example", "/api"), rule, ip);
        foreach (var mutation in second.ExportMutations()) first.Merge(mutation);
        Assert.False(checker.CheckRequest(Context("a.example", "/other"), ip).IsTriggered);
    }

    [Fact]
    public async Task CaptchaMiddlewareUsesScopedSubjectAndOriginalPath()
    {
        var rule = new AdvancedCcRule { Id = Guid.NewGuid().ToString("N"), Threshold = 1, Action = CcAction.Captcha };
        var provider = DispatchProxy.Create<IStatisticService, CcRemoteUnbanTests.RuleProvider>();
        ((CcRemoteUnbanTests.RuleProvider)(object)provider).Rules = [rule];
        using var store = Store("a");
        var log = new CcBanLog();
        using var decisions = new CcDecisionService(provider, store, log);
        var checker = new CcRuleChecker(provider, null!, store, decisions);
        checker.CheckRequest(Context("a.example", "/api"), "192.0.2.87");
        var captcha = DispatchProxy.Create<ICaptchaService, CaptchaSpy>();
        using var services = new ServiceCollection().AddSingleton<ICcRuleChecker>(checker).BuildServiceProvider();
        var passed = false;
        var middleware = new CaptchaMiddleware(_ => { passed = true; return Task.CompletedTask; }, captcha);
        var other = Context("b.example", "/api");
        other.RequestServices = services;
        await middleware.InvokeAsync(other);
        Assert.True(passed);
        var page = Context("a.example", "/api");
        page.RequestServices = services;
        await middleware.InvokeAsync(page);
        Assert.Equal(403, page.Response.StatusCode);
        page.Response.Body.Position = 0;
        Assert.Contains("ccCaptchaUrl('challenge')", await new StreamReader(page.Response.Body).ReadToEndAsync());
        var challenge = Context("a.example", "/api");
        challenge.RequestServices = services;
        challenge.Request.QueryString = new QueryString("?__cc_captcha=challenge");
        await middleware.InvokeAsync(challenge);
        Assert.Equal(checker.GetCaptchaSubject(challenge, "192.0.2.87"), ((CaptchaSpy)(object)captcha).Subject);
        Assert.Equal(1, Assert.Single(log.Read()).Nodes!["a"].InterceptedRequests);
    }

    [Fact]
    public void SuccessfulCaptchaClearsOnlyItsScopedRuleAndCounters()
    {
        var rule = new AdvancedCcRule { Id = Guid.NewGuid().ToString("N"), Threshold = 2, Action = CcAction.Captcha };
        var provider = DispatchProxy.Create<IStatisticService, CcRemoteUnbanTests.RuleProvider>();
        ((CcRemoteUnbanTests.RuleProvider)(object)provider).Rules = [rule];
        using var store = Store("verify");
        var checker = new CcRuleChecker(provider, null!, store);
        const string ip = "192.0.2.87";
        foreach (var host in new[] { "a.example", "b.example" })
        {
            checker.CheckRequest(Context(host, "/api"), ip);
            checker.CheckRequest(Context(host, "/api"), ip);
        }
        using var services = new ServiceCollection().AddSingleton<ICcRuleChecker>(checker).BuildServiceProvider();
        var captcha = new CaptchaService(services, new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())), store);
        var subject = checker.GetCaptchaSubject(Context("a.example", "/api"), ip)!;
        var token = Guid.NewGuid().ToString("N");
        store.Set("captcha.challenge", token, new CaptchaService.CaptchaEntry
        { ClientIp = subject, Answer = "42", ExpiryTime = DateTime.UtcNow.AddMinutes(1) });
        Assert.True(captcha.Verify(subject, token, "42", new BehaviorData { Duration = 2000, MouseTrackCount = 4 }).Success);
        Assert.Null(checker.GetCaptchaSubject(Context("a.example", "/api"), ip));
        Assert.NotNull(checker.GetCaptchaSubject(Context("b.example", "/api"), ip));
        Assert.False(checker.CheckRequest(Context("a.example", "/api"), ip).IsTriggered);
    }

    public class CaptchaSpy : DispatchProxy
    {
        public string? Subject;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(ICaptchaService.IsPending)) return false;
            if (method?.Name == nameof(ICaptchaService.GenerateChallenge))
            {
                Subject = (string)args![0]!;
                return new CaptchaChallenge();
            }
            if (method?.Name == nameof(ICaptchaService.GenerateChallengeAsync))
            {
                Subject = (string)args![0]!;
                return Task.FromResult(new CaptchaChallenge());
            }
            throw new NotSupportedException();
        }
    }

    private static DefaultHttpContext Context(string host, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.87");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static RuntimeStateStore Store(string node) => new(new StaticOptionsMonitor<RuntimeStateOptions>(CcReleaseTestKeys.Configure(new()
    { NodeId = node, StateDirectory = Path.Combine(Path.GetTempPath(), "cc-scope-" + Guid.NewGuid().ToString("N")) })));
}
