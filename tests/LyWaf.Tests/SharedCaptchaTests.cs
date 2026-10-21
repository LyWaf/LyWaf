using System.Collections.Concurrent;
using LyWaf.Config;
using LyWaf.Services.Captcha;
using LyWaf.Services.Distribution;
using LyWaf.Services.Metrics;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using LyWaf.Services.AntiBot;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Tests;

public sealed class SharedCaptchaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProofChallengeCanOnlyBeConsumedOnce(bool shared)
    {
        using var fixture = new Fixture(new Store());
        var options = new StaticOptionsMonitor<AntiBotOptions>(new()
            { Secret = new string('x', 40), ProofLeadingZeroNibbles = 1 });
        var first = new AntiBotChallengeService(options, shared ? fixture.Services : null);
        var second = new AntiBotChallengeService(options, shared ? fixture.Services : null);
        var token = await first.CreateChallengeAsync(Context());
        long counter = 0;
        while (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + ":" + counter))).StartsWith('0')) counter++;
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            (i % 2 == 0 ? first : second).VerifyProofAsync(Context(), token, counter))));
        Assert.Single(results, r => r.Success);
        Assert.False((await second.VerifyProofAsync(Context(), token, counter)).Success);
    }

    private static HttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new("captcha.test");
        context.Request.Headers.UserAgent = "test-browser";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return context;
    }

    [Fact]
    public async Task SharedChallengeCanBeConsumedByAnotherServiceOnlyOnceWithoutRuntimeCopies()
    {
        var shared = new Store();
        using var fixture = new Fixture(shared);
        var challenge = await fixture.First.GenerateChallengeAsync("192.0.2.10");
        Assert.StartsWith("r1.", challenge.Token);
        Assert.False(fixture.Runtime.TryGet<CaptchaService.CaptchaEntry>("captcha.challenge", challenge.Token, out _));
        var entry = shared.Items[challenge.Token];
        var behavior = new BehaviorData { Duration = 2000, MouseTrackCount = 4, SliderOffset = entry.SliderTarget };
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            (i % 2 == 0 ? fixture.First : fixture.Second).VerifyAsync("192.0.2.10", challenge.Token, entry.Answer, behavior, null))));
        Assert.Single(results, r => r.Success);
    }

    [Fact]
    public async Task StoreFailureNeverConsumesLegacyRuntimeCopyOrClearsPending()
    {
        var shared = new Store { Fail = true };
        using var fixture = new Fixture(shared);
        fixture.First.SetPending("192.0.2.11", "test", 60);
        const string token = "legacy";
        fixture.Runtime.Set("captcha.challenge", token, new CaptchaService.CaptchaEntry
            { ClientIp = "192.0.2.11", Answer = "42", ExpiryTime = DateTime.UtcNow.AddMinutes(1) });
        var result = await fixture.First.VerifyAsync("192.0.2.11", token, "42", new() { Duration = 2000, MouseTrackCount = 4 }, null);
        Assert.False(result.Success);
        Assert.Equal(503, result.FailureStatus);
        Assert.Equal("captcha_store_unavailable", result.ErrorCode);
        Assert.True(fixture.First.IsPending("192.0.2.11"));
        Assert.True(fixture.Runtime.TryGet<CaptchaService.CaptchaEntry>("captcha.challenge", token, out _));
        await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => fixture.First.GenerateChallengeAsync("192.0.2.11"));
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeVerificationSuccess()
    {
        using var fixture = new Fixture(new Store());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.First.GenerateChallengeAsync("ip", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.First.VerifyAsync("ip", "token", "42", null, null, cancelled.Token));
    }

    [Theory]
    [InlineData("Local", true, false)]
    [InlineData("Local", false, true)]
    [InlineData("Authority", true, true)]
    [InlineData("Redis", true, false)]
    [InlineData("Unknown", false, false)]
    public void ConfigurationRequiresSharedStoreForMultiNode(string provider, bool distributed, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NodeCommunication:Enabled"] = distributed.ToString(), ["CaptchaStore:Provider"] = provider,
            ["NodeCommunication:ClusterId"] = "test",
            ["CaptchaStore:ClusterId"] = "test", ["CaptchaStore:AuthorityNodeId"] = "authority"
        }).Build();
        using var services = new ServiceCollection().AddLyWafCaptcha(config).BuildServiceProvider();
        if (valid) Assert.Equal(provider, services.GetRequiredService<IOptions<CaptchaStoreOptions>>().Value.Provider);
        else Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<CaptchaStoreOptions>>().Value);
    }

    [Fact]
    public void LyConfigurationParsesAndDistributionPreservesLocalStoreCredentials()
    {
        const string local = """
            CaptchaStore {
                Provider = "Authority"
                ClusterId = "shared-cluster"
                AuthorityNodeId = "local-authority"
                StateDirectory = "local-state"
                TimeoutMilliseconds = 1500
                MaxChallenges = 100
            }
            Protect { Enabled = true }
            """;
        var yaml = LyToAppSettingsConverter.Convert(local);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml));
        var config = new ConfigurationBuilder().AddYamlStream(stream).Build();
        Assert.Equal("Authority", config["CaptchaStore:Provider"]);
        Assert.Equal("1500", config["CaptchaStore:TimeoutMilliseconds"]);
        var composed = NodeLocalConfigurationOverlay.Compose("config.ly", local,
            local.Replace("local-authority", "remote-authority"));
        Assert.Contains("local-authority", composed);
        Assert.DoesNotContain("remote-authority", composed);
    }

    private sealed class Store : ISharedCaptchaStore
    {
        public ConcurrentDictionary<string, CaptchaService.CaptchaEntry> Items { get; } = new();
        public bool Fail;
        public Task PutAsync(string token, CaptchaService.CaptchaEntry entry, CancellationToken tokenCancellation, int lifetimeMilliseconds = 300000)
        {
            if (Fail) throw new CaptchaStoreUnavailableException();
            Assert.True(Items.TryAdd(token, entry));
            return Task.CompletedTask;
        }
        public Task<CaptchaService.CaptchaEntry?> TakeAsync(string token, CancellationToken tokenCancellation)
        {
            if (Fail) throw new CaptchaStoreUnavailableException();
            return Task.FromResult(Items.TryRemove(token, out var entry) ? entry : null);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "shared-captcha-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _services;
        public ServiceProvider Services => _services;
        public RuntimeStateStore Runtime { get; }
        public CaptchaService First { get; }
        public CaptchaService Second { get; }
        public Fixture(ISharedCaptchaStore shared)
        {
            _services = new ServiceCollection().AddSingleton(shared).BuildServiceProvider();
            Runtime = new(new StaticOptionsMonitor<RuntimeStateOptions>(new() { StateDirectory = _directory }));
            var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
            First = new(_services, metrics, Runtime);
            Second = new(_services, metrics, Runtime);
        }
        public void Dispose()
        {
            Runtime.Dispose();
            _services.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
