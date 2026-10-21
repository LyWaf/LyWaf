using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSocketChannelTests
{
    [Fact]
    public async Task SendingAfterDisconnectIsTransportFailureNotCallerCancellation()
    {
        await using var pair = await Pair.CreateAsync();
        await pair.A.DisposeAsync();
        var error = await Assert.ThrowsAsync<IOException>(() => pair.A.RequestAsync([1], CancellationToken.None));
        Assert.Contains("unknown", error.Message);
    }

    [Fact]
    public async Task MultiplexesBothDirectionsAndNestedRequests()
    {
        await using var pair = await Pair.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var a = pair.A.RunAsync((body, _) => Task.FromResult(body), timeout.Token);
        var b = pair.B.RunAsync((body, ct) => pair.B.RequestAsync(body, ct), timeout.Token);
        try
        {
            var requests = Enumerable.Range(0, 20).Select(async n =>
            {
                var bytes = BitConverter.GetBytes(n);
                Assert.Equal(bytes, await pair.A.RequestAsync(bytes, timeout.Token));
            });
            await Task.WhenAll(requests);
            Assert.Equal(new byte[] { 42 }, await pair.B.RequestAsync([42], timeout.Token));
        }
        finally { timeout.Cancel(); await IgnoreShutdown(a, b); }
    }

    [Fact]
    public async Task CancelledRequestDoesNotPoisonConnection()
    {
        await using var pair = await Pair.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = pair.A.RunAsync((body, _) => Task.FromResult(body), timeout.Token);
        var b = pair.B.RunAsync(async (body, ct) =>
        {
            if (body[0] == 0) { entered.TrySetResult(); await Task.Delay(200, ct); }
            return body;
        }, timeout.Token);
        try
        {
            using var caller = new CancellationTokenSource();
            var cancelled = pair.A.RequestAsync([0], caller.Token);
            await entered.Task.WaitAsync(timeout.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal(new byte[] { 1 }, await pair.A.RequestAsync([1], timeout.Token));
        }
        finally { timeout.Cancel(); await IgnoreShutdown(a, b); }
    }

    [Fact]
    public async Task CapacityAndOversizedRequestsAreRejectedWithoutBreakingHealthyRequests()
    {
        await using var pair = await Pair.CreateAsync(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = pair.A.RunAsync((body, _) => Task.FromResult(body), timeout.Token);
        var b = pair.B.RunAsync((_, ct) => { entered.TrySetResult(); return release.Task.WaitAsync(ct); }, timeout.Token);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => pair.A.RequestAsync(new byte[2048], timeout.Token));
            var first = pair.A.RequestAsync([1], timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await Assert.ThrowsAsync<IOException>(() => pair.A.RequestAsync([2], timeout.Token));
            release.SetResult([3]);
            Assert.Equal(new byte[] { 3 }, await first);
        }
        finally { timeout.Cancel(); await IgnoreShutdown(a, b); }
    }

    [Fact]
    public async Task DisconnectFailsPendingRequestWithoutClaimingItWasNotExecuted()
    {
        await using var pair = await Pair.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = pair.A.RunAsync((body, _) => Task.FromResult(body), timeout.Token);
        var b = pair.B.RunAsync(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<byte>();
        }, timeout.Token);
        try
        {
            var request = pair.A.RequestAsync([1], timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await pair.B.DisposeAsync();
            var error = await Assert.ThrowsAsync<IOException>(() => request);
            Assert.Contains("unknown", error.Message);
        }
        finally { timeout.Cancel(); await IgnoreShutdown(a, b); }
    }

    [Fact]
    public async Task HandlerFailureIsReportedAndConnectionRemainsUsable()
    {
        await using var pair = await Pair.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var a = pair.A.RunAsync((body, _) => Task.FromResult(body), timeout.Token);
        var b = pair.B.RunAsync((body, _) => body[0] == 0
            ? throw new InvalidOperationException("private server details") : Task.FromResult(body), timeout.Token);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => pair.A.RequestAsync([0], timeout.Token));
            Assert.DoesNotContain("private", error.Message);
            Assert.Equal(new byte[] { 1 }, await pair.A.RequestAsync([1], timeout.Token));
        }
        finally { timeout.Cancel(); await IgnoreShutdown(a, b); }
    }

    private static async Task IgnoreShutdown(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException) { }
        }
    }

    private sealed class Pair(TcpClient client, TcpClient server, NodeSocketChannel a, NodeSocketChannel b) : IAsyncDisposable
    {
        public NodeSocketChannel A => a;
        public NodeSocketChannel B => b;
        public static async Task<Pair> CreateAsync(int capacity = 32)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var server = await listener.AcceptTcpClientAsync();
            return new(client, server,
                new(WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.FromSeconds(5)), 1024, capacity),
                new(WebSocket.CreateFromStream(server.GetStream(), true, null, TimeSpan.FromSeconds(5)), 1024, capacity));
        }
        public async ValueTask DisposeAsync()
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
            client.Dispose();
            server.Dispose();
        }
    }
}
