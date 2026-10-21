using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LyWaf.Services.Metrics;
using LyWaf.Services.Retry;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Transformer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace LyWaf.Tests;

public sealed class CcIngressYarpTests
{
    [Fact]
    public async Task RealTlsWafTransformsAndYarpRetriesPreserveOneCountPerExternalRequest()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        await using var c = await Hop.Start("c", cert);
        await using var b = await Hop.Start("b", cert, c);
        await using var a = await Hop.Start("a", cert, b);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == cert.Thumbprint };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        for (var i = 1; i <= 3; i++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, a.Url + "/api");
            message.Headers.TryAddWithoutValidation(CcIngressOwnership.Header, "forged-external-token");
            using var response = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(i * 3, c.Deliveries);
            Assert.Equal(i, a.Count);
            Assert.Equal(0, b.Count);
            Assert.Equal(0, c.Count);
        }
        // A direct client bypassing the authorized upstream still counts locally.
        using var direct = await client.GetAsync(c.Url + "/api");
        Assert.Equal(1, c.Count);
    }

    private sealed class Hop : IAsyncDisposable
    {
        private const string Secret = "tls-yarp-cc-pairwise-test-secret-32bytes";
        private readonly CcIngressOptions _ingress = new() { Enabled = true };
        private WebApplication _app = null!;
        private RuntimeStateStore _store = null!;
        public string Url = "";
        public int Deliveries;
        public long Count => _store.GetSnapshots("cc.access").Sum(s => s.CounterValue ?? 0);
        public static async Task<Hop> Start(string id, X509Certificate2 cert, Hop? downstream = null)
        {
            var hop = new Hop();
            var runtime = new RuntimeStateOptions { Enabled = true, NodeId = id,
                StateDirectory = Path.Combine(Path.GetTempPath(), "cc-yarp-" + Guid.NewGuid().ToString("N")) };
            hop._store = new(new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
            var rule = new AdvancedCcRule { Id = "yarp", ProtectionGroup = "tls", Threshold = 1000, Period = 3600 };
            var statistics = DispatchProxy.Create<IStatisticService, CcCohortHttpTests.Rules>();
            ((CcCohortHttpTests.Rules)(object)statistics).Rule = rule;
            var ownership = new CcIngressOwnership(new StaticOptionsMonitor<CcIngressOptions>(hop._ingress), new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
            var checker = new CcRuleChecker(statistics, null!, hop._store, ingress: ownership);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(cert)));
            builder.Services.AddSingleton(ownership);
            if (downstream is not null)
            {
                hop._ingress.Peers[id == "a" ? "b" : "c"] = Grant(downstream.Url);
                downstream._ingress.Peers[id] = Grant("");
                builder.Services.AddSingleton<IWafInfoService>(new WafFixture());
                builder.Services.AddSingleton(new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));
                builder.Services.AddReverseProxy().LoadFromMemory(
                    [new RouteConfig { RouteId = "api", ClusterId = "next", Match = new() { Path = "/api" } }],
                    [new ClusterConfig { ClusterId = "next", Destinations = new Dictionary<string, DestinationConfig>
                        { ["next"] = new() { Address = downstream.Url } },
                        // Isolated loopback test listeners only; no machine-wide trust changes.
                        HttpClient = new() { DangerousAcceptAnyServerCertificate = true },
                        Metadata = new Dictionary<string, string> { ["Retry.Enabled"] = "true", ["Retry.MaxAttempts"] = "3",
                            ["Retry.BaseBackoffMs"] = "0", ["Retry.BudgetMinRetriesPerSecond"] = "100" } }]).AddTransforms<WafTrans>();
                builder.Services.AddSingleton<IForwarderHttpClientFactory, RetryForwarderHttpClientFactory>();
            }
            hop._app = builder.Build();
            hop._app.Use(async (context, next) => { checker.CheckRequest(context, "192.0.2.1"); await next(context); });
            if (downstream is null) hop._app.MapGet("/api", () =>
                Interlocked.Increment(ref hop.Deliveries) % 3 == 0 ? Results.Ok() : Results.StatusCode(503));
            else hop._app.MapReverseProxy();
            await hop._app.StartAsync(); hop.Url = hop._app.Urls.Single(); return hop;
        }
        private static CcIngressPeer Grant(string destination) => new() { Secret = Secret, DestinationAuthority = destination,
            RemoteAddresses = ["127.0.0.1"], RuleIds = ["yarp"], Scopes = ["group:tls"] };
        public async ValueTask DisposeAsync() { await _app.DisposeAsync(); _store.Dispose(); }
    }
    private sealed class WafFixture : IWafInfoService
    {
        public WafInfoOptions GetOptions() => new();
        public X509Certificate2 GetCertByName(string name) => throw new NotSupportedException();
        public X509Certificate2? TryGetCertByName(string name) => null;
    }
}
