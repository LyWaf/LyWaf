using System.Net;
using LyWaf.Policy;
using LyWaf.Services.Metrics;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Tests;

public sealed class ActiveHealthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReservedLocalResponseClusterNeverEnablesProbing(bool? enabled)
    {
        var cluster = new ClusterConfig
        {
            ClusterId = "cluster_unuse",
            HealthCheck = enabled.HasValue ? new() { Active = new() { Enabled = enabled } } : null,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["dest1"] = new() { Address = "http://0.0.0.0" }
            }
        };
        var filtered = await new DefaultActiveHealthConfigFilter().ConfigureClusterAsync(cluster, default);
        Assert.False(filtered.HealthCheck!.Active!.Enabled);
        Assert.Same(cluster.Destinations, filtered.Destinations);
    }

    [Fact]
    public async Task MissingActiveSettingsReceiveDefaultsAndExplicitDisableIsPreserved()
    {
        var filter = new DefaultActiveHealthConfigFilter();
        var result = await filter.ConfigureClusterAsync(new ClusterConfig { ClusterId = "one" }, default);
        var active = result.HealthCheck!.Active!;
        Assert.True(active.Enabled);
        Assert.Equal("LyxActiveHealth", active.Policy);
        Assert.Equal("/", active.Path);
        Assert.Equal(TimeSpan.FromSeconds(30), active.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), active.Timeout);
        var disabled = result with { HealthCheck = result.HealthCheck with { Active = active with { Enabled = false } } };
        Assert.Same(disabled, await filter.ConfigureClusterAsync(disabled, default));
        var custom = result with { HealthCheck = new() { Active = active with { Policy = "ConsecutiveFailures", Path = "/ready" }, Passive = new() { Enabled = true } } };
        var preserved = await filter.ConfigureClusterAsync(custom, default);
        Assert.Equal(custom.HealthCheck, preserved.HealthCheck);
    }

    private sealed class Updater : IDestinationHealthUpdater
    {
        public NewActiveDestinationHealth[] Last = [];
        public void SetActive(ClusterState cluster, IEnumerable<NewActiveDestinationHealth> states) => Last = states.ToArray();
        public void SetPassive(ClusterState cluster, DestinationState destination, DestinationHealth health, TimeSpan period) { }
    }
    private static ClusterState Cluster(string id, Dictionary<string, string>? metadata = null) =>
        new(id, new ClusterModel(new ClusterConfig { ClusterId = id, Metadata = metadata }, new HttpMessageInvoker(new HttpClientHandler())));
    private static DestinationState Destination() => new("origin", new DestinationModel(new DestinationConfig { Address = "http://localhost:8080/" }));
    private static LyxActiveHealthPolicy Policy(Updater updater) => new(updater, new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));
    private static void Probe(LyxActiveHealthPolicy policy, ClusterState cluster, DestinationState destination, HttpStatusCode status, HttpContent? content = null)
    {
        using var response = new HttpResponseMessage(status) { Content = content ?? new StringContent("ok") };
        policy.ProbingCompleted(cluster, [new DestinationProbingResult(destination, response, null)]);
    }

    [Fact]
    public void SuccessFailureAndRecoveryRequireConsecutiveResultsAndRetainKnownState()
    {
        var updater = new Updater(); var policy = Policy(updater); var cluster = Cluster("one", new()); var destination = Destination();
        Probe(policy, cluster, destination, HttpStatusCode.OK);
        Probe(policy, cluster, destination, HttpStatusCode.OK);
        Assert.Equal(new NewActiveDestinationHealth(destination, DestinationHealth.Healthy), Assert.Single(updater.Last));
        Probe(policy, cluster, destination, HttpStatusCode.InternalServerError);
        Assert.Equal(new NewActiveDestinationHealth(destination, DestinationHealth.Healthy), Assert.Single(updater.Last));
        Probe(policy, cluster, destination, HttpStatusCode.InternalServerError);
        Assert.Equal(new NewActiveDestinationHealth(destination, DestinationHealth.Unhealthy), Assert.Single(updater.Last));
        Probe(policy, cluster, destination, HttpStatusCode.OK);
        Assert.Equal(new NewActiveDestinationHealth(destination, DestinationHealth.Unhealthy), Assert.Single(updater.Last));
        Probe(policy, cluster, destination, HttpStatusCode.OK);
        Assert.Equal(new NewActiveDestinationHealth(destination, DestinationHealth.Healthy), Assert.Single(updater.Last));
    }

    [Fact]
    public void SharedAddressesDoNotShareCounters()
    {
        var updater = new Updater(); var policy = Policy(updater); var one = Destination(); var two = Destination();
        Probe(policy, Cluster("one"), one, HttpStatusCode.OK);
        Probe(policy, Cluster("two"), two, HttpStatusCode.OK);
        Assert.Equal(new NewActiveDestinationHealth(two, DestinationHealth.Unknown), Assert.Single(updater.Last));
    }

    [Fact]
    public void ContainsCheckCompletesBeforeCallbackReturns()
    {
        var updater = new Updater(); var policy = Policy(updater); var destination = Destination();
        var cluster = Cluster("one", new() { ["LyxActiveHealth.Passes"] = "1", ["LyxActiveHealth.AvalidContent"] = "ok" });
        Probe(policy, cluster, destination, HttpStatusCode.OK, new DelayedContent());
        Assert.Equal(new NewActiveDestinationHealth(destination, DestinationHealth.Healthy), Assert.Single(updater.Last));
    }

    private sealed class DelayedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await Task.Delay(30);
            await stream.WriteAsync("status ok now"u8.ToArray());
        }
    }

    [Fact]
    public void ProbeUsesDedicatedHealthAddressAndNormalizedQuery()
    {
        var cluster = new ClusterModel(new ClusterConfig { ClusterId = "one", HealthCheck = new()
            { Active = new() { Enabled = true, Path = "/ready", Query = "?full=1" } } }, new HttpMessageInvoker(new HttpClientHandler()));
        using var request = new LyxProbingRequestFactory().CreateRequest(cluster, new DestinationModel(new DestinationConfig
            { Address = "http://localhost:8080", Health = "http://localhost:9090/base/" }));
        Assert.Equal("http://localhost:9090/base/ready?full=1", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task YarpSchedulerProbesWithoutTrafficAndUpdatesHealthAfterOriginChanges()
    {
        var status = 204;
        var calls = 0;
        var originBuilder = WebApplication.CreateBuilder();
        originBuilder.Logging.ClearProviders();
        originBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var origin = originBuilder.Build();
        origin.MapPost("/ready", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            if (await reader.ReadToEndAsync() != "{\"probe\":true}" || context.Request.ContentType != "application/json; charset=utf-8")
                return Results.BadRequest();
            Interlocked.Increment(ref calls);
            return Results.StatusCode(Volatile.Read(ref status));
        });
        await origin.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));
        builder.Services.AddSingleton<IActiveHealthCheckPolicy, LyxActiveHealthPolicy>();
        builder.Services.AddSingleton<IProbingRequestFactory, LyxProbingRequestFactory>();
        builder.Services.AddReverseProxy().AddConfigFilter<DefaultActiveHealthConfigFilter>().LoadFromMemory(
            [new RouteConfig { RouteId = "route", ClusterId = "cluster", Match = new() { Path = "/{**path}" } }],
            [new ClusterConfig { ClusterId = "cluster", Destinations = new Dictionary<string, DestinationConfig>
                { ["origin"] = new() { Address = origin.Urls.Single() } },
                Metadata = new Dictionary<string, string> { ["LyxActiveHealth.Passes"] = "1", ["LyxActiveHealth.Fails"] = "1",
                    ["LyxActiveHealth.Method"] = "POST", ["LyxActiveHealth.Body"] = "{\"probe\":true}",
                    ["LyxActiveHealth.ContentType"] = "application/json", ["LyxActiveHealth.AvalidCode"] = "204" },
                HealthCheck = new() { Active = new() { Path = "/ready",
                    Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromSeconds(1) } } }]);
        await using var proxy = builder.Build();
        proxy.MapGet("/probe-test-alive", () => Results.Ok());
        proxy.MapReverseProxy();
        try
        {
            await proxy.StartAsync();
            var lookup = proxy.Services.GetRequiredService<IProxyStateLookup>();
            async Task WaitFor(DestinationHealth health)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (lookup.GetClusters().Single().Destinations["origin"].Health.Active != health)
                    await Task.Delay(25, timeout.Token);
            }
            await WaitFor(DestinationHealth.Healthy);
            Volatile.Write(ref status, 200);
            await WaitFor(DestinationHealth.Unhealthy);
            Volatile.Write(ref status, 204);
            await WaitFor(DestinationHealth.Healthy);
            Assert.True(Volatile.Read(ref calls) >= 3);
            // Stop the actual listener to exercise HttpClient connection refusal, not an HTTP error.
            await origin.StopAsync();
            await WaitFor(DestinationHealth.Unhealthy);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var alive = await client.GetAsync(proxy.Urls.Single() + "/probe-test-alive");
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        }
        finally { await proxy.StopAsync(); await origin.StopAsync(); }
    }
}
