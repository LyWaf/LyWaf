using System.Net;
using System.Net.Sockets;
using System.Text;
using LyWaf.Services.StreamServer;
using Xunit;

namespace LyWaf.Tests;

public sealed class StreamRuntimeTests
{
    [Fact]
    public async Task TcpHandlerSendsProxyHeaderAndRelaysBothDirections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamEndpoint = (IPEndPoint)upstream.LocalEndpoint;
        var ingress = new TcpListener(IPAddress.Loopback, 0);
        ingress.Start();
        var config = new StreamConfig { Upstreams = [$"127.0.0.1:{upstreamEndpoint.Port}"], SendProxyProtocol = true };
        var handler = new StreamHandler(new StreamServerOptions(), config, "test");

        using var client = new TcpClient();
        var accept = ingress.AcceptSocketAsync(timeout.Token);
        await client.ConnectAsync((IPEndPoint)ingress.LocalEndpoint, timeout.Token);
        using var accepted = await accept;
        var handling = handler.HandleAsync(accepted, timeout.Token);
        using var upstreamClient = await upstream.AcceptTcpClientAsync(timeout.Token);
        var upstreamStream = upstreamClient.GetStream();
        var proxyLine = await ReadLineAsync(upstreamStream, timeout.Token);
        Assert.StartsWith("PROXY TCP4 127.0.0.1 127.0.0.1 ", proxyLine);

        var clientStream = client.GetStream();
        await clientStream.WriteAsync("ping"u8.ToArray(), timeout.Token);
        var request = new byte[4];
        await upstreamStream.ReadExactlyAsync(request, timeout.Token);
        Assert.Equal("ping", Encoding.ASCII.GetString(request));
        await upstreamStream.WriteAsync("pong"u8.ToArray(), timeout.Token);
        var response = new byte[4];
        await clientStream.ReadExactlyAsync(response, timeout.Token);
        Assert.Equal("pong", Encoding.ASCII.GetString(response));
        client.Close();
        await handling;
        ingress.Stop();
        upstream.Stop();
    }

    [Fact]
    public async Task UdpProxyPreservesDatagramAndReturnsResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var upstreamEndpoint = (IPEndPoint)upstream.Client.LocalEndPoint!;
        var config = new StreamConfig
        {
            Protocol = StreamProtocol.Udp,
            Upstreams = [$"127.0.0.1:{upstreamEndpoint.Port}"],
            UdpSessionTimeout = 10
        };
        using var proxy = new UdpStreamProxy(new IPEndPoint(IPAddress.Loopback, 0), "test", config, null);
        var running = proxy.RunAsync(timeout.Token);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync("query"u8.ToArray(), proxy.LocalEndpoint, timeout.Token);
        var received = await upstream.ReceiveAsync(timeout.Token);
        Assert.Equal("query", Encoding.ASCII.GetString(received.Buffer));
        await upstream.SendAsync("answer"u8.ToArray(), received.RemoteEndPoint, timeout.Token);
        var answer = await client.ReceiveAsync(timeout.Token);
        Assert.Equal("answer", Encoding.ASCII.GetString(answer.Buffer));
        await timeout.CancelAsync();
        await running;
    }

    [Fact]
    public async Task UdpProxyAppliesConfiguredRateLimitWithoutSplittingDatagrams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var config = new StreamConfig
        {
            Protocol = StreamProtocol.Udp,
            Upstreams = [$"127.0.0.1:{((IPEndPoint)upstream.Client.LocalEndPoint!).Port}"],
            BytesPerSecond = 100,
            UdpSessionTimeout = 10
        };
        using var proxy = new UdpStreamProxy(new IPEndPoint(IPAddress.Loopback, 0), "rate-test", config, null);
        var running = proxy.RunAsync(timeout.Token);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var started = System.Diagnostics.Stopwatch.StartNew();
        await client.SendAsync(new byte[20], proxy.LocalEndpoint, timeout.Token);
        var received = await upstream.ReceiveAsync(timeout.Token);

        Assert.Equal(20, received.Buffer.Length);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(140), $"Elapsed: {started.Elapsed}");
        await timeout.CancelAsync();
        await running;
    }

    [Fact]
    public async Task TcpHandlerRoutesBySniAndForwardsOriginalClientHello()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fallback = new TcpListener(IPAddress.Loopback, 0);
        var selected = new TcpListener(IPAddress.Loopback, 0);
        var ingress = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start(); selected.Start(); ingress.Start();
        var fallbackPort = ((IPEndPoint)fallback.LocalEndpoint).Port;
        var selectedPort = ((IPEndPoint)selected.LocalEndpoint).Port;
        var config = new StreamConfig
        {
            Upstreams = [$"127.0.0.1:{fallbackPort}"],
            SniPreread = true,
            SniUpstreams = new(StringComparer.OrdinalIgnoreCase) { ["api.example.com"] = [$"127.0.0.1:{selectedPort}"] }
        };
        var handler = new StreamHandler(new StreamServerOptions(), config, "sni-test");
        using var client = new TcpClient();
        var acceptedTask = ingress.AcceptSocketAsync(timeout.Token);
        await client.ConnectAsync((IPEndPoint)ingress.LocalEndpoint, timeout.Token);
        using var accepted = await acceptedTask;
        var handling = handler.HandleAsync(accepted, timeout.Token);
        var hello = StreamPrimitivesTests.ClientHello("api.example.com");
        await client.GetStream().WriteAsync(hello, timeout.Token);
        using var target = await selected.AcceptTcpClientAsync(timeout.Token);
        var received = new byte[hello.Length];
        await target.GetStream().ReadExactlyAsync(received, timeout.Token);
        Assert.Equal(hello, received);
        Assert.False(fallback.Pending());
        client.Close();
        await handling;
        ingress.Stop(); fallback.Stop(); selected.Stop();
    }

    [Fact]
    public async Task AcceptedProxyHeaderUsesDeclaredAddressForAccessControl()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        var ingress = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start(); ingress.Start();
        var config = new StreamConfig
        {
            Upstreams = [$"127.0.0.1:{((IPEndPoint)upstream.LocalEndpoint).Port}"],
            AcceptProxyProtocol = true,
            ProxyProtocolTrustedNetworks = ["127.0.0.0/8"],
            AllowedNetworks = ["203.0.113.0/24"]
        };
        var handler = new StreamHandler(new StreamServerOptions(), config, "proxy-input");
        using var client = new TcpClient();
        var acceptedTask = ingress.AcceptSocketAsync(timeout.Token);
        await client.ConnectAsync((IPEndPoint)ingress.LocalEndpoint, timeout.Token);
        using var accepted = await acceptedTask;
        var handling = handler.HandleAsync(accepted, timeout.Token);
        await client.GetStream().WriteAsync("PROXY TCP4 203.0.113.8 192.0.2.1 4567 9000\r\nping"u8.ToArray(), timeout.Token);
        using var target = await upstream.AcceptTcpClientAsync(timeout.Token);
        var payload = new byte[4];
        await target.GetStream().ReadExactlyAsync(payload, timeout.Token);
        Assert.Equal("ping", Encoding.ASCII.GetString(payload));
        client.Close();
        await handling;
        ingress.Stop(); upstream.Stop();
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        while (bytes.Count < ProxyProtocolV1.MaxHeaderBytes)
        {
            var value = new byte[1];
            await stream.ReadExactlyAsync(value, token);
            bytes.Add(value[0]);
            if (bytes.Count >= 2 && bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("PROXY header too long");
    }
}
