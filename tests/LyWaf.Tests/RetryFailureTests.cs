using System.Net;
using LyWaf.Policy;
using LyWaf.Services.Metrics;
using LyWaf.Services.Retry;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace LyWaf.Tests;

public sealed class RetryFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwarderHandlesRealTransportCancellation(bool callerCancelled)
    {
        using var services = new ServiceCollection().AddLogging().AddHttpForwarder().BuildServiceProvider();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var abort = new CancellationTokenSource();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        var circuit = new CircuitBreakerState("test", CircuitBreakerPolicy.FromMetadata(null), metrics);
        using var client = new HttpMessageInvoker(new CircuitBreakerHandler("test", circuit, metrics)
        {
            InnerHandler = new RetryHandler("test", RetryPolicy.Disabled, metrics, circuit)
            {
                InnerHandler = new SocketsHttpHandler { UseProxy = false }
            }
        });
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/";
        context.Request.Protocol = "HTTP/1.1";
        context.RequestAborted = abort.Token;
        using var body = new MemoryStream();
        context.Response.Body = body;
        var pending = services.GetRequiredService<IHttpForwarder>().SendAsync(context,
            $"http://127.0.0.1:{port}/", client,
            new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMilliseconds(500) }, HttpTransformer.Default);
        using var connection = await listener.AcceptTcpClientAsync(limit.Token);
        // Keep the upstream silent to exercise cancellation inside SocketsHttpHandler.
        if (callerCancelled) abort.Cancel();
        var result = await pending.AsTask().WaitAsync(limit.Token);
        Assert.Equal(callerCancelled ? ForwarderError.RequestCanceled : ForwarderError.RequestTimedOut, result);
        if (!callerCancelled) Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
    }

    [Fact]
    public async Task CancelledProbeReturns504InsteadOfPropagatingCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var client = Client(new FailedTransport(new TaskCanceledException()));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/");
        request.Options.Set(HealthProbeRequestOptions.IsProbe, true);
        using var response = await client.SendAsync(request, cancelled.Token);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    private sealed class FailedTransport(Exception failure) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(failure);
        }
    }

    private static HttpMessageInvoker Client(HttpMessageHandler transport, RetryPolicy? policy = null)
    {
        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        return new HttpMessageInvoker(new RetryHandler("test", policy ?? RetryPolicy.Disabled, metrics,
            new CircuitBreakerState("test", CircuitBreakerPolicy.FromMetadata(null), metrics)) { InnerHandler = transport });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionFailureBecomes502WithAndWithoutRetries(bool retries)
    {
        var transport = new FailedTransport(new HttpRequestException("connection refused"));
        var policy = retries ? RetryPolicy.FromMetadata(new Dictionary<string, string>
        {
            ["Retry.Enabled"] = "true", ["Retry.MaxAttempts"] = "2", ["Retry.BaseBackoffMs"] = "0",
            ["Retry.BudgetMinRetriesPerSecond"] = "100"
        }) : RetryPolicy.Disabled;
        using var client = Client(transport, policy);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/");
        using var response = await client.SendAsync(request, default);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Same(request, response.RequestMessage);
        Assert.Equal(retries ? 2 : 1, transport.Calls);
    }

    [Fact]
    public async Task TimeoutBecomes504ButCallerCancellationPropagates()
    {
        using var client = Client(new FailedTransport(new TaskCanceledException()));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/");
        using var response = await client.SendAsync(request, default);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var pending = client.SendAsync(request, cancelled.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(pending.IsCanceled);
        Assert.Equal(cancelled.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RealInvalidUpstreamResponseReturns502()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = listener.AcceptTcpClientAsync(limit.Token).AsTask();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = Client(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        var pending = client.SendAsync(request, limit.Token);
        using var connection = await accepted;
        // Exercise a real HttpClient failure without relying on firewall-specific closed-port behavior.
        await connection.GetStream().WriteAsync("INVALID HTTP RESPONSE\r\n\r\n"u8.ToArray(), limit.Token);
        using var response = await pending;
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task RealProbeTimeoutReturns504()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = listener.AcceptTcpClientAsync(limit.Token).AsTask();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = Client(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        request.Options.Set(HealthProbeRequestOptions.IsProbe, true);
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        var pending = client.SendAsync(request, probeTimeout.Token);
        using var connection = await accepted;
        // Accept the connection but send no HTTP response, then expire the probe's deadline.
        probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        using var response = await pending;
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }
}
