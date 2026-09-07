using System.Text.Json;
using LyWaf.Services.Discovery;
using Xunit;
using Yarp.ReverseProxy.Configuration;
using Microsoft.Extensions.Options;

namespace LyWaf.Tests;

public sealed class ServiceDiscoveryTests
{
    [Fact]
    public async Task FileProviderReadsAndNormalizesDestinations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lywaf-discovery-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                destinations = new[] { new { id = "node-a", address = "http://127.0.0.1:8080", metadata = new { zone = "east" } } }
            }));
            var provider = new FileDiscoveryProvider();
            var result = await provider.DiscoverAsync("file", new DiscoveryProviderOptions { Path = path }, default);

            var destination = Assert.Single(result);
            Assert.Equal("http://127.0.0.1:8080/", destination.Address);
            Assert.Equal("east", destination.Metadata!["zone"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FileProviderRejectsRelativeOrNonHttpAddress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lywaf-discovery-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"destinations\":[{\"id\":\"bad\",\"address\":\"file:///tmp/a\"}]}");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new FileDiscoveryProvider().DiscoverAsync("file", new DiscoveryProviderOptions { Path = path }, default));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DnsProviderPublishesLoopbackAddressFamilies()
    {
        var result = await new DnsDiscoveryProvider().DiscoverAsync("dns", new DiscoveryProviderOptions
        {
            Host = "localhost", Port = 8080, Scheme = "http", IncludeIPv4 = true, IncludeIPv6 = true
        }, default);

        Assert.NotEmpty(result);
        Assert.All(result, x => Assert.StartsWith("http://", x.Address));
        Assert.Equal(result.Count, result.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void RegistryKeepsVersionedImmutableSnapshot()
    {
        var registry = new DiscoveryRegistry();
        var destinations = new[] { new DiscoveredDestination("a", "http://127.0.0.1/") };
        var first = new DiscoverySnapshot("cluster", "file", DiscoveryVersion.Create(destinations), DateTimeOffset.UtcNow, destinations);

        Assert.True(registry.Publish(first));
        Assert.False(registry.Publish(first with { ObservedAt = first.ObservedAt.AddSeconds(1) }));
        Assert.Same(first, registry.Get("cluster"));
    }

    [Fact]
    public void DockerProviderFiltersLabelsAndPublishesContainerNetwork()
    {
        using var document = JsonDocument.Parse("""
            [{"Id":"abcdef1234567890","Labels":{"app":"api"},"NetworkSettings":{"Networks":{"bridge":{"IPAddress":"172.18.0.3"}}}}]
            """);
        var result = DockerDiscoveryProvider.ParseContainers(document.RootElement, "docker",
            new DiscoveryProviderOptions { Selector = "app=api", Port = 8080, Scheme = "http" });

        var destination = Assert.Single(result);
        Assert.Equal("http://172.18.0.3:8080/", destination.Address);
        Assert.Equal("bridge", destination.Metadata!["docker.network"]);
    }

    [Fact]
    public void KubernetesProviderSkipsUnreadyEndpointSliceAddresses()
    {
        using var document = JsonDocument.Parse("""
            {"items":[{"metadata":{"name":"api-x1"},"ports":[{"port":8080}],"endpoints":[
              {"conditions":{"ready":true},"addresses":["10.1.0.2"]},
              {"conditions":{"ready":false},"addresses":["10.1.0.3"]}
            ]}]}
            """);
        var result = KubernetesDiscoveryProvider.ParseEndpointSlices(document.RootElement,
            new DiscoveryProviderOptions { Namespace = "prod", Service = "api", Scheme = "http", Port = 0 });

        var destination = Assert.Single(result);
        Assert.Equal("http://10.1.0.2:8080/", destination.Address);
        Assert.Equal("api", destination.Metadata!["kubernetes.service"]);
    }

    [Fact]
    public async Task YarpFilterReplacesOnlyClusterWithPublishedSnapshot()
    {
        var registry = new DiscoveryRegistry();
        var destinations = new[] { new DiscoveredDestination("dynamic", "http://127.0.0.1:9000/") };
        registry.Publish(new DiscoverySnapshot("api", "file", DiscoveryVersion.Create(destinations), DateTimeOffset.UtcNow, destinations));
        var filter = new DiscoveryProxyConfigFilter(registry);
        var original = new ClusterConfig
        {
            ClusterId = "api",
            Destinations = new Dictionary<string, DestinationConfig> { ["static"] = new() { Address = "http://127.0.0.1:8000/" } }
        };

        var updated = await filter.ConfigureClusterAsync(original, default);

        Assert.Single(updated.Destinations!);
        Assert.Equal("http://127.0.0.1:9000/", updated.Destinations!["dynamic"].Address);
    }

    [Fact]
    public void KubernetesIngressAndGatewayApiResolveBackendServices()
    {
        using var ingress = JsonDocument.Parse("""
            {"spec":{"defaultBackend":{"service":{"name":"fallback"}},"rules":[{"http":{"paths":[{"backend":{"service":{"name":"api"}}}]}}]}}
            """);
        using var route = JsonDocument.Parse("""
            {"spec":{"rules":[{"backendRefs":[{"name":"api"},{"name":"worker","kind":"Service"},{"name":"bucket","kind":"S3"}]}]}}
            """);

        Assert.Equal(["api", "fallback"], KubernetesDiscoveryProvider.ResolveServiceNames(ingress.RootElement, "Ingress", ""));
        Assert.Equal(["api", "worker"], KubernetesDiscoveryProvider.ResolveServiceNames(route.RootElement, "GatewayApi", ""));
    }

    [Fact]
    public void SrvParserReadsCompressedOwnerAndTarget()
    {
        // Header + _api._tcp.test question + one SRV answer whose owner is a compression pointer.
        byte[] packet =
        [
            0x12,0x34, 0x81,0x80, 0x00,0x01, 0x00,0x01, 0x00,0x00, 0x00,0x00,
            0x04,(byte)'_',(byte)'a',(byte)'p',(byte)'i', 0x04,(byte)'_',(byte)'t',(byte)'c',(byte)'p',
            0x04,(byte)'t',(byte)'e',(byte)'s',(byte)'t', 0x00, 0x00,0x21, 0x00,0x01,
            0xC0,0x0C, 0x00,0x21, 0x00,0x01, 0x00,0x00,0x00,0x1E, 0x00,0x0C,
            0x00,0x0A, 0x00,0x05, 0x1F,0x90,
            0x03,(byte)'a',(byte)'p',(byte)'i', 0xC0,0x16
        ];

        var record = Assert.Single(DnsDiscoveryProvider.ParseSrvPacket(packet, 0x1234));
        Assert.Equal((ushort)10, record.Priority);
        Assert.Equal((ushort)5, record.Weight);
        Assert.Equal((ushort)8080, record.Port);
        Assert.Equal("api.test", record.Target);
    }

    [Fact]
    public void AddressParserReadsIpv4AndTtl()
    {
        byte[] packet =
        [
            0x12,0x35, 0x81,0x80, 0x00,0x01, 0x00,0x01, 0x00,0x00, 0x00,0x00,
            0x01,(byte)'a', 0x04,(byte)'t',(byte)'e',(byte)'s',(byte)'t', 0x00, 0x00,0x01, 0x00,0x01,
            0xC0,0x0C, 0x00,0x01, 0x00,0x01, 0x00,0x00,0x00,0x1E, 0x00,0x04,
            192,0,2,10
        ];
        var record = Assert.Single(DnsDiscoveryProvider.ParseAddressResponse(packet, 0x1235, 1));
        Assert.Equal("192.0.2.10", record.Address.ToString());
        Assert.Equal(30U, record.Ttl);
    }

    [Fact]
    public async Task CoordinatorUsesDnsTtlInsteadOfFixedRefreshInterval()
    {
        var provider = new TtlProvider();
        var options = new ServiceDiscoveryOptions
        {
            Enabled = true, DebounceMilliseconds = 0,
            Providers = new Dictionary<string, DiscoveryProviderOptions>
            {
                ["dns"] = new() { Type = "Ttl", ClusterId = "api", RefreshSeconds = 60, DnsMinTtlSeconds = 1 }
            }
        };
        var coordinator = new DiscoveryCoordinator(new StaticOptionsMonitor<ServiceDiscoveryOptions>(options),
            [provider], new DiscoveryRegistry(), new DiscoveryReloadSignal());
        await coordinator.PollAsync(options, default);
        await Task.Delay(1100);
        await coordinator.PollAsync(options, default);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CoordinatorRetainsLastSnapshotWhenProviderFails()
    {
        var provider = new SequenceProvider();
        var options = new ServiceDiscoveryOptions
        {
            Enabled = true,
            DebounceMilliseconds = 0,
            Providers = new Dictionary<string, DiscoveryProviderOptions>
            {
                ["test"] = new() { Type = "Test", ClusterId = "api", RefreshSeconds = 1 }
            }
        };
        var registry = new DiscoveryRegistry();
        var coordinator = new DiscoveryCoordinator(new StaticOptionsMonitor<ServiceDiscoveryOptions>(options),
            [provider], registry, new DiscoveryReloadSignal());

        await coordinator.PollAsync(options, default);
        var valid = registry.Get("api");
        Assert.NotNull(valid);
        await Task.Delay(1100);
        provider.Fail = true;
        await coordinator.PollAsync(options, default);

        Assert.Same(valid, registry.Get("api"));
    }

    private sealed class SequenceProvider : IServiceDiscoveryProvider
    {
        public string Type => "Test";
        public bool Fail { get; set; }
        public Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(string providerId, DiscoveryProviderOptions options, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("temporary failure");
            return Task.FromResult<IReadOnlyList<DiscoveredDestination>>(
                [new DiscoveredDestination("node", "http://127.0.0.1:8080/")]);
        }
    }

    private sealed class TtlProvider : IServiceDiscoveryProvider
    {
        public string Type => "Ttl";
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(string providerId,
            DiscoveryProviderOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DiscoveredDestination>>([new("node", "http://127.0.0.1:8080/",
                new Dictionary<string, string> { ["dns.ttl_seconds"] = "1" })]);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
