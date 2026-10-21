using LyWaf.Services.Captcha;
using LyWaf.Services.Metrics;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class CaptchaConcurrentConsumptionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "captcha-race-" + Guid.NewGuid().ToString("N"));
    private RuntimeStateStore CreateStore() => new(new StaticOptionsMonitor<RuntimeStateOptions>(new() { NodeId = "test", StateDirectory = _directory }));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedBetweenLocalAndRuntimeRemovalAllowsOnlyOneSuccess(bool separateService)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var store = CreateStore();
        using var blocked = new BlockingRemoval(store);
        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        var firstService = new CaptchaService(services, metrics, blocked);
        var secondService = separateService ? new CaptchaService(services, metrics, blocked) : firstService;
        var ip = "192.0.2.175";
        var challenge = firstService.GenerateChallenge(ip);
        Assert.True(store.TryGet<CaptchaService.CaptchaEntry>("captcha.challenge", challenge.Token, out var entry));
        var behavior = new BehaviorData { Duration = 2000, MouseTrackCount = 4, SliderOffset = entry!.SliderTarget };
        blocked.Token = challenge.Token;
        Task<CaptchaVerifyResult>? first = null, second = null;
        using var secondStarted = new ManualResetEventSlim();
        try
        {
            first = Task.Factory.StartNew(() => firstService.Verify(ip, challenge.Token, entry.Answer, behavior),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(5)));
            second = Task.Factory.StartNew(() =>
            {
                secondStarted.Set();
                return secondService.Verify(ip, challenge.Token, entry.Answer, behavior);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            // The old implementation completes through the runtime copy while the first removal is paused.
            await Task.WhenAny(second, Task.Delay(1000));
        }
        finally
        {
            blocked.Release.Set();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var results = await Task.WhenAll(first!, second!);
        Assert.Single(results, result => result.Success);
        Assert.False(firstService.Verify(ip, challenge.Token, entry.Answer, behavior).Success);
        Assert.False(store.TryGet<CaptchaService.CaptchaEntry>("captcha.challenge", challenge.Token, out _));
    }

    [Fact]
    public async Task RuntimeOnlyChallengeRemainsSingleUseUnderConcurrentVerification()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var store = CreateStore();
        var captcha = new CaptchaService(services, new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())), store);
        var token = Guid.NewGuid().ToString("N");
        store.Set("captcha.challenge", token, new CaptchaService.CaptchaEntry
            { Mode = "math", Answer = "42", ClientIp = "192.0.2.176", ExpiryTime = DateTime.UtcNow.AddMinutes(1) });
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => captcha.VerifyAsync(
            "192.0.2.176", token, "42", new() { Duration = 2000, MouseTrackCount = 4 }, null))));
        Assert.Single(results, result => result.Success);
    }

    [Fact]
    public void FailedClientBindingStillConsumesBothCopies()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var store = CreateStore();
        var captcha = new CaptchaService(services, new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())), store);
        var challenge = captcha.GenerateChallenge("192.0.2.177");
        Assert.True(store.TryGet<CaptchaService.CaptchaEntry>("captcha.challenge", challenge.Token, out var entry));
        var behavior = new BehaviorData { Duration = 2000, MouseTrackCount = 4, SliderOffset = entry!.SliderTarget };
        Assert.False(captcha.Verify("192.0.2.178", challenge.Token, entry.Answer, behavior).Success);
        Assert.False(captcha.Verify("192.0.2.177", challenge.Token, entry.Answer, behavior).Success);
    }

    private sealed class BlockingRemoval(IRuntimeStateStore inner) : IRuntimeStateStore, IDisposable
    {
        public string? Token { get; set; }
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Remove(string domain, string key)
        {
            if (domain == "captcha.challenge" && key == Token)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release removal");
            }
            return inner.Remove(domain, key);
        }
        public void Set<T>(string domain, string key, T value, TimeSpan? timeToLive = null) => inner.Set(domain, key, value, timeToLive);
        public bool TryGet<T>(string domain, string key, out T? value) => inner.TryGet(domain, key, out value);
        public bool TryTake<T>(string domain, string key, out T? value) => inner.TryTake(domain, key, out value);
        public long IncrementWindow(string domain, string key, DateTimeOffset start, TimeSpan length, long amount = 1) => inner.IncrementWindow(domain, key, start, length, amount);
        public long GetWindowValue(string domain, string key, DateTimeOffset start) => inner.GetWindowValue(domain, key, start);
        public bool TryClaim(string domain, string key, TimeSpan ttl) => inner.TryClaim(domain, key, ttl);
        public bool Merge(RuntimeStateMutation mutation, bool replicate = false) => inner.Merge(mutation, replicate);
        public IReadOnlyList<RuntimeStateSnapshot> GetSnapshots(string? domain = null) => inner.GetSnapshots(domain);
        public IReadOnlyList<(string Key, T Value)> GetValues<T>(string domain) => inner.GetValues<T>(domain);
        public RuntimeStateStatus GetStatus() => inner.GetStatus();
        public CcPolicyGeneration GetCcPolicyGeneration(string key) => inner.GetCcPolicyGeneration(key);
        public CcPolicyGeneration AdvanceCcPolicyGeneration(string key, CcPolicyGeneration? expected = null) => inner.AdvanceCcPolicyGeneration(key, expected);
        public RuntimeStateMutation IssueCcCaptchaRelease(string key, CcPolicyGeneration expected, string id) => inner.IssueCcCaptchaRelease(key, expected, id);
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
