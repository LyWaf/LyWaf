using System.Net;
using LyWaf.Policy;
using LyWaf.Services.Metrics;
using LyWaf.Services.Retry;
using Xunit;

namespace LyWaf.Tests;

public sealed class RetryFailureTests
{
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

    private static HttpMessageInvoker Client(FailedTransport transport, RetryPolicy? policy = null)
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancelled.Token));
    }
}
