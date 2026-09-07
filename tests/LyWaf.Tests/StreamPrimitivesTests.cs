using System.Net;
using System.Buffers.Binary;
using System.Text;
using LyWaf.Services.StreamServer;
using LyWaf.Services.Metrics;
using LyWaf.Plugins.Core;
using LyWaf.Config;
using Xunit;

namespace LyWaf.Tests;

public sealed class StreamPrimitivesTests
{
    [Theory]
    [InlineData("db.example.com:5432", "db.example.com", 5432)]
    [InlineData("[2001:db8::1]:443", "2001:db8::1", 443)]
    public void ParsesIpv4HostnameAndBracketedIpv6(string value, string host, int port)
    {
        Assert.True(StreamPrimitives.TryParseEndpoint(value, out var endpoint));
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("host:0")]
    [InlineData("2001:db8::1:443")]
    public void RejectsAmbiguousOrInvalidEndpoints(string value) =>
        Assert.False(StreamPrimitives.TryParseEndpoint(value, out _));

    [Fact]
    public void DeniedNetworkTakesPriorityOverAllowedNetwork()
    {
        var address = IPAddress.Parse("10.2.3.4");
        Assert.False(StreamPrimitives.IsClientAllowed(address, ["10.0.0.0/8"], ["10.2.0.0/16"]));
        Assert.True(StreamPrimitives.IsClientAllowed(IPAddress.Parse("10.3.0.1"), ["10.0.0.0/8"], ["10.2.0.0/16"]));
        Assert.False(StreamPrimitives.IsClientAllowed(IPAddress.Parse("192.168.1.1"), ["10.0.0.0/8"], []));
    }

    [Fact]
    public void ValidatesStreamSecurityAndResourceLimits()
    {
        Assert.True(StreamPrimitives.IsValidNetwork("2001:db8::/32"));
        Assert.False(StreamPrimitives.IsValidNetwork("10.0.0.0/99"));
        Assert.False(StreamPrimitives.IsValidConfig(new StreamConfig
        {
            Upstreams = ["127.0.0.1:80"], AcceptProxyProtocol = true
        }));
        Assert.False(StreamPrimitives.IsValidConfig(new StreamConfig
        {
            Protocol = StreamProtocol.Udp, Upstreams = ["127.0.0.1:53"], SniPreread = true
        }));
        Assert.False(StreamPrimitives.IsValidConfig(new StreamConfig
        {
            Protocol = StreamProtocol.Udp, Upstreams = ["127.0.0.1:53"], SendProxyProtocol = true
        }));
    }

    [Fact]
    public void SelectorsHonorRoundRobinLeastConnectionsAndStableClientHash()
    {
        var selector = new StreamUpstreamSelector();
        string[] upstreams = ["a:1", "b:2"];
        Assert.Equal("a:1", selector.Select(upstreams, StreamLoadBalancePolicy.RoundRobin, null));
        Assert.Equal("b:2", selector.Select(upstreams, StreamLoadBalancePolicy.RoundRobin, null));
        using var lease = selector.Track("a:1");
        Assert.Equal("b:2", selector.Select(upstreams, StreamLoadBalancePolicy.LeastConnections, null));
        var first = selector.Select(upstreams, StreamLoadBalancePolicy.ClientIpHash, IPAddress.Parse("203.0.113.9"));
        Assert.Equal(first, selector.Select(upstreams, StreamLoadBalancePolicy.ClientIpHash, IPAddress.Parse("203.0.113.9")));
        Assert.Equal(1, selector.Active("a:1"));
        lease.Dispose();
        Assert.Equal(0, selector.Active("a:1"));
    }

    [Fact]
    public void ProxyProtocolV1RoundTripsIpv4Endpoints()
    {
        var source = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 12345);
        var destination = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 443);
        var line = ProxyProtocolV1.Create(source, destination);

        Assert.True(ProxyProtocolV1.TryParse(System.Text.Encoding.ASCII.GetBytes(line), out var parsedSource, out var parsedDestination, out var consumed));
        Assert.Equal(source, parsedSource);
        Assert.Equal(destination, parsedDestination);
        Assert.Equal(line.Length, consumed);
    }

    [Fact]
    public void ProxyProtocolRejectsOversizedOrMalformedInput()
    {
        Assert.False(ProxyProtocolV1.TryParse(new byte[ProxyProtocolV1.MaxHeaderBytes + 1], out _, out _, out _));
        Assert.False(ProxyProtocolV1.TryParse("PROXY TCP4 bad bad 1 2\r\n"u8, out _, out _, out _));
    }

    [Fact]
    public void ExtractsSniFromBoundedClientHello()
    {
        var hello = ClientHello("Api.Example.com");
        Assert.Equal(hello.Length, TlsClientHelloParser.RequiredRecordBytes(hello));
        Assert.True(TlsClientHelloParser.TryGetServerName(hello, out var serverName));
        Assert.Equal("api.example.com", serverName);
        Assert.False(TlsClientHelloParser.TryGetServerName(hello.AsSpan(0, hello.Length - 1), out _));
    }

    [Fact]
    public void ResolvesExactThenLongestWildcardSniRoute()
    {
        var config = new StreamConfig
        {
            Upstreams = ["default:443"],
            SniUpstreams = new(StringComparer.OrdinalIgnoreCase)
            {
                ["*.example.com"] = ["wildcard:443"],
                ["*.api.example.com"] = ["specific:443"],
                ["login.example.com"] = ["login:443"]
            }
        };
        Assert.Equal("login:443", Assert.Single(StreamPrimitives.ResolveSniUpstreams(config, "login.example.com")));
        Assert.Equal("specific:443", Assert.Single(StreamPrimitives.ResolveSniUpstreams(config, "v1.api.example.com")));
        Assert.Equal("default:443", Assert.Single(StreamPrimitives.ResolveSniUpstreams(config, "other.test")));
    }

    internal static byte[] ClientHello(string host)
    {
        var name = Encoding.ASCII.GetBytes(host);
        var serverNames = new byte[3 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(serverNames.AsSpan(1, 2), (ushort)name.Length);
        name.CopyTo(serverNames, 3);
        var extension = new byte[4 + 2 + serverNames.Length];
        BinaryPrimitives.WriteUInt16BigEndian(extension.AsSpan(2, 2), (ushort)(2 + serverNames.Length));
        BinaryPrimitives.WriteUInt16BigEndian(extension.AsSpan(4, 2), (ushort)serverNames.Length);
        serverNames.CopyTo(extension, 6);
        var body = new byte[34 + 1 + 2 + 2 + 1 + 1 + 2 + extension.Length];
        body[0] = 3; body[1] = 3;
        var offset = 34;
        body[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset, 2), 2); offset += 2;
        body[offset++] = 0x13; body[offset++] = 0x01;
        body[offset++] = 1; body[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset, 2), (ushort)extension.Length); offset += 2;
        extension.CopyTo(body, offset);
        var handshake = new byte[4 + body.Length];
        handshake[0] = 1;
        handshake[1] = (byte)(body.Length >> 16); handshake[2] = (byte)(body.Length >> 8); handshake[3] = (byte)body.Length;
        body.CopyTo(handshake, 4);
        var record = new byte[5 + handshake.Length];
        record[0] = 22; record[1] = 3; record[2] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3, 2), (ushort)handshake.Length);
        handshake.CopyTo(record, 5);
        return record;
    }

    [Fact]
    public void PrometheusRendersBoundedStreamMetrics()
    {
        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        metrics.RecordStreamEvent("127.0.0.1:9000", "TCP", "accepted");
        metrics.ChangeStreamActive("127.0.0.1:9000", "tcp", 1);
        metrics.AddStreamBytes("127.0.0.1:9000", "tcp", "upstream", 42);
        metrics.SetStreamUdpSessions("127.0.0.1:9001", 2);
        metrics.SetCertificateStatus("api.example.com", DateTime.UnixEpoch.AddSeconds(1234), "acme", "valid");

        var output = metrics.Render(false, new EmptyPluginManager());

        Assert.Contains("lywaf_stream_events_total{listen=\"127.0.0.1:9000\",protocol=\"tcp\",outcome=\"accepted\"} 1", output);
        Assert.Contains("lywaf_stream_connections_active{listen=\"127.0.0.1:9000\",protocol=\"tcp\"} 1", output);
        Assert.Contains("lywaf_stream_bytes_total{listen=\"127.0.0.1:9000\",protocol=\"tcp\",direction=\"upstream\"} 42", output);
        Assert.Contains("lywaf_stream_udp_sessions{listen=\"127.0.0.1:9001\",protocol=\"udp\"} 2", output);
        Assert.Contains("lywaf_certificate_expiry_timestamp_seconds{domain=\"api.example.com\",source=\"acme\"} 1234", output);
        Assert.Contains("lywaf_certificate_status{domain=\"api.example.com\",status=\"valid\"} 1", output);
    }

    [Fact]
    public void LyConfigMapsAdvancedStreamOptions()
    {
        var parsed = LyConfigParser.Parse("""
            StreamServer {
                9000 {
                    Upstreams = ["one:9000", "two:9000"]
                    Protocol = "udp"
                    Policy = "client_ip_hash"
                    AcceptProxyProtocol = true
                    SendProxyProtocol = true
                    ProxyProtocolTrustedNetworks = ["10.0.0.0/8"]
                    SniPreread = true
                    SniUpstreams { api.example.com = ["tls:443"] }
                    AllowedNetworks = ["192.0.2.0/24"]
                    DeniedNetworks = ["192.0.2.9/32"]
                    MaxConnections = 50
                    BytesPerSecond = 1048576
                    UdpSessionTimeout = 45
                    MaxUdpSessions = 500
                }
            }
            """);
        var transformed = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var server = Assert.IsType<Dictionary<string, object>>(transformed["StreamServer"]);
        var streams = Assert.IsType<Dictionary<string, object>>(server["Streams"]);
        var stream = Assert.IsType<Dictionary<string, object>>(streams["9000"]);

        Assert.Equal("Udp", stream["Protocol"]);
        Assert.Equal("ClientIpHash", stream["Policy"]);
        Assert.Equal(true, stream["AcceptProxyProtocol"]);
        Assert.Equal(50, stream["MaxConnections"]);
        Assert.Equal(1048576L, stream["BytesPerSecond"]);
        Assert.Equal(500, stream["MaxUdpSessions"]);
        var sni = Assert.IsType<Dictionary<string, List<string>>>(stream["SniUpstreams"]);
        Assert.Equal("tls:443", Assert.Single(sni["api.example.com"]));
    }

    [Fact]
    public void HealthCheckerRegistersSniTargetsAndStopsDuringInitialDelay()
    {
        var options = new StreamServerOptions
        {
            Streams = new()
            {
                ["9000"] = new StreamConfig
                {
                    Upstreams = ["default:9000"],
                    SniUpstreams = new() { ["api.example.com"] = ["sni:9443"] }
                }
            }
        };
        using var checker = new StreamHealthChecker(options);
        checker.Start();
        Assert.Contains("default:9000", checker.GetAllHealthStatus().Keys);
        Assert.Contains("sni:9443", checker.GetAllHealthStatus().Keys);
        checker.Stop();
    }

    private sealed class EmptyPluginManager : IPluginManager
    {
        public ILyWafPlugin? GetPlugin(string pluginId) => null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [];
        public bool IsSystemPlugin(string pluginId) => false;
        public Task EnablePluginAsync(string pluginId) => Task.CompletedTask;
        public Task DisablePluginAsync(string pluginId) => Task.CompletedTask;
        public Task ReloadPluginAsync(string pluginId) => Task.CompletedTask;
        public PluginState GetPluginState(string pluginId) => PluginState.Unloaded;
    }
}
