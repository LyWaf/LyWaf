using System.Diagnostics;
using System.Net;
using LyWaf.Policy;
using LyWaf.Services.Metrics;
using LyWaf.Services.Telemetry;
using Yarp.ReverseProxy.Forwarder;

namespace LyWaf.Services.Retry;

public sealed class RetryForwarderHttpClientFactory(PrometheusMetrics metrics) : ForwarderHttpClientFactory
{
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        var policy = CircuitBreakerPolicy.FromMetadata(context.NewMetadata);
        if (policy.Enabled) handler.MaxConnectionsPerServer = policy.MaxConnections;
    }

    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler)
    {
        var circuit = new CircuitBreakerState(context.ClusterId,
            CircuitBreakerPolicy.FromMetadata(context.NewMetadata), metrics);
        var retry = new RetryHandler(context.ClusterId, RetryPolicy.FromMetadata(context.NewMetadata), metrics, circuit)
            { InnerHandler = handler };
        return new CircuitBreakerHandler(context.ClusterId, circuit, metrics) { InnerHandler = retry };
    }
}

internal sealed class CircuitBreakerHandler(
    string clusterId,
    CircuitBreakerState state,
    PrometheusMetrics metrics) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendHandledAsync(request, cancellationToken).Unwrap();

    private async Task<Task<HttpResponseMessage>> SendHandledAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            if (!await state.EnterAsync(cancellationToken))
            {
                metrics.RecordRetry(clusterId, request.RequestUri?.Authority, "circuit_rejected", 1);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    ReasonPhrase = "LyWaf circuit breaker capacity exceeded"
                });
            }
            try { return Task.FromResult(await base.SendAsync(request, cancellationToken)); }
            finally { state.Exit(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }
}

internal sealed class RetryHandler(
    string clusterId,
    RetryPolicy clusterPolicy,
    PrometheusMetrics metrics,
    CircuitBreakerState circuit) : DelegatingHandler
{
    private readonly RetryBudget _budget = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendHandledAsync(request, cancellationToken).Unwrap();

    // Preserve cancellation for YARP without rethrowing an expected cancellation in user code.
    private async Task<Task<HttpResponseMessage>> SendHandledAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return Task.FromResult(await SendWithRetriesAsync(request, cancellationToken)); }
        catch (HttpRequestException) when (HealthProbeRequestOptions.Contains(request) || !cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FailureResponse(request, HttpStatusCode.BadGateway));
        }
        catch (OperationCanceledException) when (HealthProbeRequestOptions.Contains(request) || !cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FailureResponse(request, HttpStatusCode.GatewayTimeout));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    private HttpResponseMessage FailureResponse(HttpRequestMessage request, HttpStatusCode status)
    {
        metrics.RecordRetry(clusterId, Authority(request), status == HttpStatusCode.BadGateway ? "upstream_unavailable" : "upstream_timeout", 1);
        return new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(status == HttpStatusCode.BadGateway ? "Bad Gateway" : "Gateway Timeout")
        };
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var policy = request.Options.TryGetValue(RetryRequestOptions.Policy, out var routePolicy)
            ? routePolicy : clusterPolicy;
        if (!CanRetry(request, policy)) return await base.SendAsync(request, cancellationToken);

        _budget.RecordRequest();
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(policy.TotalTimeoutMs);
        Exception? lastException = null;
        var retrySlotHeld = false;

        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            var retryAuthorized = false;
            var currentRequest = attempt == 1 ? request : CloneWithoutBody(request);
            using var tryCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            tryCts.CancelAfter(policy.PerTryTimeoutMs);
            using var activity = LyWafTelemetry.ActivitySource.StartActivity("proxy.retry.attempt", ActivityKind.Internal);
            activity?.SetTag("lywaf.retry.cluster", clusterId);
            activity?.SetTag("lywaf.retry.attempt", attempt);

            try
            {
                var response = await base.SendAsync(currentRequest, tryCts.Token);
                var statusCode = (int)response.StatusCode;
                var retryableStatus = policy.StatusCodes.Contains(statusCode);
                if (attempt >= policy.MaxAttempts || !retryableStatus)
                {
                    if (attempt > 1)
                    {
                        var outcome = retryableStatus ? "exhausted_status_" + statusCode : "completed";
                        metrics.RecordRetry(clusterId, Authority(request), outcome, attempt);
                    }
                    ReattachRequest(response, request, currentRequest);
                    return response;
                }

                if (!_budget.TryAcquireRetry(policy.BudgetPercent, policy.BudgetMinRetriesPerSecond))
                {
                    metrics.RecordRetry(clusterId, Authority(request), "budget_exhausted", attempt);
                    ReattachRequest(response, request, currentRequest);
                    return response;
                }
                retryAuthorized = true;
                lastException = null;
                response.Dispose();
                metrics.RecordRetry(clusterId, Authority(request), "status_" + statusCode, attempt);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && policy.RetryNetworkErrors)
            {
                lastException = ex;
                metrics.RecordRetry(clusterId, Authority(request), totalCts.IsCancellationRequested ? "total_timeout" : "try_timeout", attempt);
            }
            catch (HttpRequestException ex) when (policy.RetryNetworkErrors)
            {
                lastException = ex;
                metrics.RecordRetry(clusterId, Authority(request), "network_error", attempt);
            }
            finally
            {
                if (attempt > 1) currentRequest.Dispose();
                if (retrySlotHeld)
                {
                    circuit.ExitRetry();
                    retrySlotHeld = false;
                }
            }

            if (attempt >= policy.MaxAttempts) break;
            if (!retryAuthorized && !_budget.TryAcquireRetry(policy.BudgetPercent, policy.BudgetMinRetriesPerSecond))
            {
                metrics.RecordRetry(clusterId, Authority(request), "budget_exhausted", attempt);
                break;
            }
            if (!circuit.TryEnterRetry())
            {
                metrics.RecordRetry(clusterId, Authority(request), "retry_concurrency_rejected", attempt);
                break;
            }
            retrySlotHeld = true;
            var delay = Math.Min(policy.MaxBackoffMs,
                policy.BaseBackoffMs * (1 << Math.Min(attempt - 1, 10)));
            try
            {
                if (delay > 0) await Task.Delay(delay, totalCts.Token);
            }
            catch
            {
                circuit.ExitRetry();
                retrySlotHeld = false;
                throw;
            }
        }

        if (lastException != null) throw lastException;
        throw new HttpRequestException("Upstream retry attempts exhausted", null, HttpStatusCode.BadGateway);
    }

    private static bool CanRetry(HttpRequestMessage request, RetryPolicy policy)
        => !HealthProbeRequestOptions.Contains(request) && policy.Enabled && policy.Methods.Contains(request.Method.Method)
            && (request.Content == null || request.Content.Headers.ContentLength == 0);

    private static HttpRequestMessage CloneWithoutBody(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in source.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return clone;
    }

    private static string Authority(HttpRequestMessage request)
        => request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "unknown";

    private static void ReattachRequest(HttpResponseMessage response, HttpRequestMessage original, HttpRequestMessage current)
    {
        if (!ReferenceEquals(original, current)) response.RequestMessage = original;
    }
}
