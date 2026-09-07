using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LyWaf.Services.AntiBot;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class AntiBotTests
{
    [Fact]
    public void AcceptsJa4OnlyFromConfiguredTlsTerminator()
    {
        var options = new AntiBotOptions { TrustedFingerprintProxies = ["10.0.0.2"] };
        var analyzer = Analyzer(options);
        var context = Context("Mozilla/5.0");
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");
        context.Request.Headers["X-LyWaf-JA4"] = "t13d1516h2_8daaf6152771_02713d6af862";
        Assert.Equal("t13d1516h2_8daaf6152771_02713d6af862", analyzer.Assess(context).Ja4Fingerprint);

        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.Null(analyzer.Assess(context).Ja4Fingerprint);
    }

    [Theory]
    [InlineData("sqlmap/1.8", AntiBotClientClass.Scanner, 100)]
    [InlineData("HeadlessChrome/140", AntiBotClientClass.Automation, 75)]
    [InlineData("curl/8.0", AntiBotClientClass.ApiClient, 10)]
    [InlineData("Googlebot/2.1", AntiBotClientClass.SearchCrawler, 20)]
    public void ClassifiesClientsWithExplainableRisk(string userAgent, AntiBotClientClass expected, int minimum)
    {
        var analyzer = Analyzer(new());
        var context = Context(userAgent);

        var result = analyzer.Assess(context);

        Assert.Equal(expected, result.ClientClass);
        Assert.True(result.RiskScore >= minimum);
        Assert.NotEmpty(result.Reasons);
        Assert.Equal(16, result.Fingerprint.Length);
    }

    [Fact]
    public void BrowserMetadataAndConfiguredBypassAffectRisk()
    {
        var options = new AntiBotOptions { BypassPaths = ["/health"] };
        var analyzer = Analyzer(options);
        var browser = Context("Mozilla/5.0");
        var risky = analyzer.Assess(browser);
        Assert.Contains("browser_missing_fetch_metadata", risky.Reasons);
        browser.Request.Path = "/health/ready";
        Assert.True(analyzer.Assess(browser).Bypassed);
    }

    [Fact]
    public void ChallengeProofAndSessionAreBoundToClientAndRejectTampering()
    {
        var options = new AntiBotOptions { Secret = "separate-antibot-test-secret", ProofLeadingZeroNibbles = 2 };
        var service = ChallengeService(options);
        var context = Context("HeadlessChrome/140");
        context.Request.Path = "/private";
        var token = service.CreateChallenge(context);
        var counter = Solve(token, 2);

        Assert.True(service.VerifyProof(context, token, counter, out var returnPath));
        Assert.Equal("/private", returnPath);
        Assert.False(service.VerifyProof(context, token + "x", counter, out _));
        var otherClient = Context("different-agent");
        Assert.False(service.VerifyProof(otherClient, token, counter, out _));

        var session = service.CreateSession(context);
        Assert.True(service.ValidateSession(context, session));
        Assert.False(service.ValidateSession(otherClient, session));
    }

    [Fact]
    public async Task MiddlewareBlocksScannerAndChallengesAutomation()
    {
        var blockOptions = new AntiBotOptions { Enabled = true, Mode = "Block", Secret = "test", BlockScore = 100 };
        var blocked = Context("sqlmap/1.8");
        await Middleware(blockOptions, _ => Task.CompletedTask).InvokeAsync(blocked);
        Assert.Equal(403, blocked.Response.StatusCode);
        Assert.Contains("antibot_blocked", Body(blocked));

        var challengeOptions = new AntiBotOptions { Enabled = true, Mode = "Challenge", Secret = "test", ChallengeScore = 60 };
        var challenged = Context("HeadlessChrome/140");
        challenged.Request.Path = "/private";
        await Middleware(challengeOptions, _ => Task.CompletedTask).InvokeAsync(challenged);
        Assert.Equal(403, challenged.Response.StatusCode);
        Assert.Contains("crypto.subtle.digest", Body(challenged));
        Assert.Equal("no-store", challenged.Response.Headers.CacheControl);
    }

    private static AntiBotAnalyzer Analyzer(AntiBotOptions options) => new(new StaticOptionsMonitor<AntiBotOptions>(options));
    private static AntiBotChallengeService ChallengeService(AntiBotOptions options) => new(new StaticOptionsMonitor<AntiBotOptions>(options));
    private static AntiBotMiddleware Middleware(AntiBotOptions options, RequestDelegate next)
    {
        var monitor = new StaticOptionsMonitor<AntiBotOptions>(options);
        return new(next, monitor, new AntiBotAnalyzer(monitor), new AntiBotChallengeService(monitor));
    }

    private static DefaultHttpContext Context(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("example.test");
        context.Request.Headers.UserAgent = userAgent;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static long Solve(string token, int zeros)
    {
        var prefix = new string('0', zeros);
        for (long counter = 0; counter < 2_000_000; counter++)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{token}:{counter}")));
            if (hash.StartsWith(prefix, StringComparison.Ordinal)) return counter;
        }
        throw new InvalidOperationException("proof not found");
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
