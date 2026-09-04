using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LyWaf.Plugins.Core;
using LyWaf.Services.Telemetry;
using LyWaf.Shared;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Metrics;

/// <summary>
/// 进程内运行指标注册表。一次记录会同时更新 Prometheus/OpenMetrics 文本序列和 OTLP Meter，
/// 因此各中间件不需要针对不同导出协议重复采集。
/// </summary>
public sealed class PrometheusMetrics(IOptionsMonitor<MetricsOptions> options)
{
    private static readonly double[] DurationBuckets =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly ConcurrentDictionary<RequestKey, RequestSeries> _requests = new();
    private readonly ConcurrentDictionary<string, AtomicLong> _requestBodyBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AtomicLong> _responseBodyBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AtomicLong> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AdminRequestKey, RequestSeries> _adminRequests = new();
    private readonly AtomicLong _adminInFlight = new();
    private readonly ConcurrentDictionary<UpstreamKey, int> _upstreamHealth = new();
    private readonly ConcurrentDictionary<UpstreamKey, int> _upstreamConcurrentRequests = new();
    private readonly ConcurrentDictionary<string, RequestSeries> _webTamperChecks = new(StringComparer.Ordinal);
    private readonly AtomicLong _webTamperQueueDepth = new();
    private readonly AtomicLong _webTamperQueueDropped = new();
    private readonly ConcurrentDictionary<DatabaseKey, RequestSeries> _databaseOperations = new();
    private readonly ConcurrentDictionary<string, AtomicLong> _retentionDeletedRows = new(StringComparer.Ordinal);
    private int _duckDbEnabled;
    private readonly ConcurrentDictionary<string, AtomicLong> _proxyErrors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<CaptchaKey, AtomicLong> _captchaEvents = new();
    private readonly ConcurrentDictionary<PluginOperationKey, AtomicLong> _pluginOperations = new();
    private readonly ConcurrentDictionary<RetryKey, AtomicLong> _retryAttempts = new();
    private readonly ConcurrentDictionary<CircuitKey, CircuitSnapshot> _circuits = new();
    private readonly ConcurrentDictionary<CircuitRejectKey, AtomicLong> _circuitRejections = new();
    private readonly ConcurrentDictionary<OutlierKey, OutlierMetricSnapshot> _outliers = new();
    private readonly ConcurrentDictionary<OutlierDecisionKey, AtomicLong> _outlierDecisions = new();
    private readonly ConcurrentDictionary<string, AtomicLong> _responseCacheResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AtomicLong> _mirrorResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RequestSeries> _externalAuthorization = new(StringComparer.Ordinal);
    private readonly AtomicLong _responseCacheEntries = new();
    private readonly AtomicLong _responseCacheBytes = new();
    private readonly ConcurrentDictionary<StreamEventKey, AtomicLong> _streamEvents = new();
    private readonly ConcurrentDictionary<StreamKey, AtomicLong> _streamActive = new();
    private readonly ConcurrentDictionary<StreamByteKey, AtomicLong> _streamBytes = new();
    private readonly ConcurrentDictionary<StreamKey, AtomicLong> _streamUdpSessions = new();
    private readonly ConcurrentDictionary<string, CertificateMetricSnapshot> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public bool Enabled => options.CurrentValue.Enabled;

    public RequestObservation StartRequest(string? routeId)
    {
        var route = NormalizeRoute(routeId);
        _inFlight.GetOrAdd(route, static _ => new AtomicLong()).Increment();
        LyWafTelemetry.ProxyInFlight.Add(1, new KeyValuePair<string, object?>("route", route));
        return new RequestObservation(this, route, Stopwatch.GetTimestamp());
    }

    /// <summary>开始观测一个管理 API 请求，完成时需释放返回的观测句柄。</summary>
    public AdminRequestObservation StartAdminRequest()
    {
        _adminInFlight.Increment();
        LyWafTelemetry.AdminInFlight.Add(1);
        return new AdminRequestObservation(this, Stopwatch.GetTimestamp());
    }

    public void SetUpstreamHealth(string? clusterId, string? destination, int health)
    {
        var key = new UpstreamKey(
            NormalizeLabel(clusterId, "unknown", 128),
            NormalizeLabel(destination, "unknown", 256));
        var value = Math.Clamp(health, -1, 1);
        _upstreamHealth[key] = value;
        LyWafTelemetry.SetUpstreamHealth(key.Cluster, key.Destination, value);
    }

    public void SetUpstreamConcurrentRequests(string? clusterId, string? destination, int count)
    {
        var key = new UpstreamKey(
            NormalizeLabel(clusterId, "unknown", 128),
            NormalizeLabel(destination, "unknown", 256));
        var value = Math.Max(0, count);
        _upstreamConcurrentRequests[key] = value;
        LyWafTelemetry.SetUpstreamConcurrency(key.Cluster, key.Destination, value);
    }

    public void RecordCaptcha(string action, string result)
    {
        var normalizedAction = action is "generated" or "verified" ? action : "other";
        var normalizedResult = result switch
        {
            "math" or "slider" or "success" or "invalid" or "expired" or "behavior" => result,
            _ => "failure"
        };
        _captchaEvents.GetOrAdd(new CaptchaKey(normalizedAction, normalizedResult), static _ => new AtomicLong()).Increment();
    }

    public void RecordPluginOperation(string? pluginId, string operation, bool success)
    {
        var plugin = NormalizeLabel(pluginId, "unknown", 128);
        var normalizedOperation = operation is "initialize" or "start" or "stop" or "enable" or "disable" or "reload"
            ? operation : "other";
        _pluginOperations.GetOrAdd(new PluginOperationKey(plugin, normalizedOperation, success),
            static _ => new AtomicLong()).Increment();
    }

    public void WebTamperQueued() => _webTamperQueueDepth.Increment();

    public void WebTamperDequeued() => _webTamperQueueDepth.DecrementFloorZero();

    public void WebTamperQueueDropped() => _webTamperQueueDropped.Increment();

    public void ObserveWebTamperCheck(string outcome, TimeSpan duration)
    {
        var normalized = outcome switch
        {
            "changed" or "unchanged" or "not_found" or "failed" or "cancelled" or "timeout" => outcome,
            _ => "failed"
        };
        _webTamperChecks.GetOrAdd(normalized, static _ => new RequestSeries(DurationBuckets.Length))
            .Observe(duration.TotalSeconds, DurationBuckets);
    }

    public void SetDuckDbEnabled(bool enabled) => Volatile.Write(ref _duckDbEnabled, enabled ? 1 : 0);

    public void ObserveDuckDbOperation(string operation, bool success, TimeSpan duration)
    {
        var normalized = operation switch
        {
            "open" or "schema" or "checkpoint" or "flush" or "query" or "retention" => operation,
            _ => "other"
        };
        _databaseOperations.GetOrAdd(new DatabaseKey(normalized, success),
                static _ => new RequestSeries(DurationBuckets.Length))
            .Observe(duration.TotalSeconds, DurationBuckets);
    }

    public void RecordDuckDbRetentionRows(string table, int rows)
    {
        if (rows <= 0) return;
        var normalized = table switch
        {
            "traffic_snapshots" or "qps_snapshots" or "bandwidth_snapshots" or "api_timing_snapshots"
                or "endpoint_stats_snapshots" or "security_timeslots" or "intercept_events"
                or "security_attack_sources" => table,
            _ => "other"
        };
        _retentionDeletedRows.GetOrAdd(normalized, static _ => new AtomicLong()).Add(rows);
    }

    public void RecordProxyError(string reason)
    {
        var normalized = NormalizeLabel(reason, "unknown", 64).ToLowerInvariant();
        _proxyErrors.GetOrAdd(normalized, static _ => new AtomicLong()).Increment();
    }

    public void RecordRetry(string? clusterId, string? destination, string outcome, int attempt)
    {
        var key = new RetryKey(
            NormalizeLabel(clusterId, "unknown", 128),
            NormalizeLabel(destination, "unknown", 256),
            NormalizeLabel(outcome, "unknown", 64).ToLowerInvariant(),
            Math.Clamp(attempt, 1, 10));
        _retryAttempts.GetOrAdd(key, static _ => new AtomicLong()).Increment();
    }

    public void SetCircuitBreakerState(string? clusterId, string state, int remaining, int queued, int active, int retries)
        => _circuits[new CircuitKey(NormalizeLabel(clusterId, "unknown", 128))] =
            new CircuitSnapshot(state, remaining, queued, active, retries);

    public void RecordCircuitBreakerRejection(string? clusterId, string reason)
        => _circuitRejections.GetOrAdd(new CircuitRejectKey(
            NormalizeLabel(clusterId, "unknown", 128), NormalizeLabel(reason, "unknown", 64)),
            static _ => new AtomicLong()).Increment();

    public void SetOutlierState(string? clusterId, string? destinationId, string? address,
        bool ejected, string reason, DateTimeOffset recoveryTime)
        => _outliers[new OutlierKey(NormalizeLabel(clusterId, "unknown", 128),
            NormalizeLabel(destinationId, "unknown", 128), NormalizeLabel(address, "unknown", 256))] =
            new OutlierMetricSnapshot(ejected, NormalizeLabel(reason, "unknown", 64), recoveryTime);

    public void RecordOutlierDecision(string? clusterId, string? destinationId, string reason)
        => _outlierDecisions.GetOrAdd(new OutlierDecisionKey(NormalizeLabel(clusterId, "unknown", 128),
            NormalizeLabel(destinationId, "unknown", 128), NormalizeLabel(reason, "unknown", 64)),
            static _ => new AtomicLong()).Increment();

    public void RecordResponseCache(string outcome)
    {
        var normalized = outcome is "hit" or "miss" or "stale" or "revalidated" or "stale_error" or "bypass"
            ? outcome : "other";
        _responseCacheResults.GetOrAdd(normalized, static _ => new AtomicLong()).Increment();
    }

    /// <summary>记录请求镜像入队或发送结果，结果值限制在固定集合内。</summary>
    public void RecordMirror(string outcome)
    {
        var normalized = outcome is "queued" or "queue_full" or "success" or "failure_status" or "timeout" or "failure"
            ? outcome : "other";
        _mirrorResults.GetOrAdd(normalized, static _ => new AtomicLong()).Increment();
        LyWafTelemetry.MirroredRequests.Add(1, new KeyValuePair<string, object?>("outcome", normalized));
    }

    /// <summary>记录外部授权结果和耗时，结果标签限制在固定集合内。</summary>
    public void ObserveExternalAuthorization(string outcome, TimeSpan duration)
    {
        var normalized = outcome is "allowed" or "denied" or "cache_hit" or "fail_open" or "fail_closed"
            ? outcome : "failure";
        _externalAuthorization.GetOrAdd(normalized, static _ => new RequestSeries(DurationBuckets.Length))
            .Observe(duration.TotalSeconds, DurationBuckets);
        var tag = new KeyValuePair<string, object?>("outcome", normalized);
        LyWafTelemetry.ExternalAuthorizationRequests.Add(1, tag);
        LyWafTelemetry.ExternalAuthorizationDuration.Record(duration.TotalSeconds, tag);
    }

    public void SetResponseCacheSize(int entries, long bytes)
    {
        _responseCacheEntries.Set(Math.Max(0, entries));
        _responseCacheBytes.Set(Math.Max(0, bytes));
    }

    public void RecordStreamEvent(string? listen, string protocol, string outcome)
    {
        var key = new StreamEventKey(NormalizeLabel(listen, "unknown", 128), NormalizeStreamProtocol(protocol),
            outcome is "accepted" or "rejected" or "connected" or "connect_failed" or "closed" ? outcome : "other");
        _streamEvents.GetOrAdd(key, static _ => new AtomicLong()).Increment();
    }

    public void ChangeStreamActive(string? listen, string protocol, int delta)
    {
        var value = _streamActive.GetOrAdd(new StreamKey(NormalizeLabel(listen, "unknown", 128), NormalizeStreamProtocol(protocol)), static _ => new AtomicLong());
        if (delta > 0) value.Add(delta); else for (var i = 0; i > delta; i--) value.DecrementFloorZero();
    }

    public void AddStreamBytes(string? listen, string protocol, string direction, long bytes)
    {
        if (bytes <= 0) return;
        var normalizedDirection = direction is "upstream" or "downstream" ? direction : "other";
        _streamBytes.GetOrAdd(new StreamByteKey(NormalizeLabel(listen, "unknown", 128), NormalizeStreamProtocol(protocol), normalizedDirection), static _ => new AtomicLong()).Add(bytes);
    }

    public void SetStreamUdpSessions(string? listen, int sessions) =>
        _streamUdpSessions.GetOrAdd(new StreamKey(NormalizeLabel(listen, "unknown", 128), "udp"), static _ => new AtomicLong()).Set(Math.Max(0, sessions));

    public void SetCertificateStatus(string? domain, DateTime expiresAt, string source, string status) =>
        _certificates[NormalizeLabel(domain, "unknown", 253)] = new CertificateMetricSnapshot(
            new DateTimeOffset(expiresAt.ToUniversalTime()).ToUnixTimeSeconds(),
            NormalizeLabel(source, "unknown", 32), NormalizeLabel(status, "unknown", 32));

    public string Render(bool openMetrics, IPluginManager pluginManager)
    {
        var builder = new StringBuilder(16 * 1024);
        AppendBuildInfo(builder);
        AppendUptime(builder);
        AppendProcess(builder);
        AppendRequests(builder);
        AppendAdminRequests(builder);
        AppendProxyErrors(builder);
        AppendRetries(builder);
        AppendCircuitBreakers(builder);
        AppendOutliers(builder);
        AppendResponseCache(builder);
        AppendMirror(builder);
        AppendExternalAuthorization(builder);
        AppendStream(builder);
        AppendCertificates(builder);
        AppendInFlight(builder);
        AppendUpstreamHealth(builder);
        AppendSecurityEvents(builder);
        AppendCaptcha(builder);
        AppendWebTamper(builder);
        AppendDuckDb(builder);
        AppendPluginState(builder, pluginManager);
        AppendPluginOperations(builder);
        if (openMetrics) builder.AppendLine("# EOF");
        return builder.ToString();
    }

    private void Complete(string route, string? method, int statusCode, long requestBytes, long responseBytes, long startedAt)
    {
        if (_inFlight.TryGetValue(route, out var inFlight)) inFlight.DecrementFloorZero();

        var elapsedSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        var normalizedMethod = NormalizeMethod(method);
        var normalizedStatus = NormalizeStatus(statusCode);
        var key = new RequestKey(route, normalizedMethod, normalizedStatus);
        _requests.GetOrAdd(key, static _ => new RequestSeries(DurationBuckets.Length))
            .Observe(elapsedSeconds, DurationBuckets);
        _requestBodyBytes.GetOrAdd(route, static _ => new AtomicLong()).Add(Math.Max(0, requestBytes));
        _responseBodyBytes.GetOrAdd(route, static _ => new AtomicLong()).Add(Math.Max(0, responseBytes));
        TagList tags = new() { { "http.request.method", normalizedMethod }, { "route", route },
            { "http.response.status_code", normalizedStatus } };
        LyWafTelemetry.ProxyInFlight.Add(-1, new KeyValuePair<string, object?>("route", route));
        LyWafTelemetry.ProxyRequests.Add(1, tags);
        LyWafTelemetry.ProxyDuration.Record(elapsedSeconds, tags);
        if (requestBytes > 0) LyWafTelemetry.ProxyRequestBytes.Add(requestBytes,
            new KeyValuePair<string, object?>("route", route));
        if (responseBytes > 0) LyWafTelemetry.ProxyResponseBytes.Add(responseBytes,
            new KeyValuePair<string, object?>("route", route));
    }

    private void CompleteAdmin(string? method, int statusCode, long startedAt)
    {
        _adminInFlight.DecrementFloorZero();
        var normalizedMethod = NormalizeMethod(method);
        var normalizedStatus = NormalizeStatus(statusCode);
        var elapsedSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        _adminRequests.GetOrAdd(new AdminRequestKey(normalizedMethod, normalizedStatus),
            static _ => new RequestSeries(DurationBuckets.Length)).Observe(elapsedSeconds, DurationBuckets);
        TagList tags = new() { { "http.request.method", normalizedMethod },
            { "http.response.status_code", normalizedStatus } };
        LyWafTelemetry.AdminInFlight.Add(-1);
        LyWafTelemetry.AdminRequests.Add(1, tags);
        LyWafTelemetry.AdminDuration.Record(elapsedSeconds, tags);
    }

    private string NormalizeRoute(string? routeId)
    {
        if (!options.CurrentValue.IncludeRoute) return "all";
        return NormalizeLabel(routeId, "unknown", 128);
    }

    private static string NormalizeMethod(string? method)
    {
        return method?.ToUpperInvariant() switch
        {
            "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" or "CONNECT" => method.ToUpperInvariant(),
            _ => "OTHER"
        };
    }

    private static string NormalizeStatus(int statusCode)
        => statusCode is >= 100 and <= 599 ? statusCode.ToString(CultureInfo.InvariantCulture) : "unknown";

    private static string NormalizeLabel(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string EscapeLabel(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void AppendBuildInfo(StringBuilder builder)
    {
        var version = typeof(PrometheusMetrics).Assembly.GetName().Version?.ToString() ?? "unknown";
        builder.AppendLine("# HELP lywaf_build_info LyWaf build information.");
        builder.AppendLine("# TYPE lywaf_build_info gauge");
        builder.Append("lywaf_build_info{version=\"").Append(EscapeLabel(version)).AppendLine("\"} 1");
    }

    private void AppendUptime(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_uptime_seconds Time since the metrics service started.");
        builder.AppendLine("# TYPE lywaf_uptime_seconds gauge");
        builder.Append("lywaf_uptime_seconds ")
            .Append((DateTime.UtcNow - _startedAt).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static void AppendProcess(StringBuilder builder)
    {
        using var process = Process.GetCurrentProcess();
        builder.AppendLine("# HELP lywaf_process_cpu_seconds_total Total process CPU time.");
        builder.AppendLine("# TYPE lywaf_process_cpu_seconds_total counter");
        builder.Append("lywaf_process_cpu_seconds_total ").Append(process.TotalProcessorTime.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine();
        builder.AppendLine("# HELP lywaf_process_working_set_bytes Process working set.");
        builder.AppendLine("# TYPE lywaf_process_working_set_bytes gauge");
        builder.Append("lywaf_process_working_set_bytes ").Append(process.WorkingSet64).AppendLine();
        builder.AppendLine("# HELP lywaf_dotnet_gc_heap_bytes Managed heap size.");
        builder.AppendLine("# TYPE lywaf_dotnet_gc_heap_bytes gauge");
        builder.Append("lywaf_dotnet_gc_heap_bytes ").Append(GC.GetTotalMemory(false)).AppendLine();
        builder.AppendLine("# HELP lywaf_dotnet_gc_collections_total Garbage collections by generation.");
        builder.AppendLine("# TYPE lywaf_dotnet_gc_collections_total counter");
        for (var generation = 0; generation <= GC.MaxGeneration; generation++)
            builder.Append("lywaf_dotnet_gc_collections_total{generation=\"").Append(generation)
                .Append("\"} ").Append(GC.CollectionCount(generation)).AppendLine();
        builder.AppendLine("# HELP lywaf_process_threads Process thread count.");
        builder.AppendLine("# TYPE lywaf_process_threads gauge");
        builder.Append("lywaf_process_threads ").Append(process.Threads.Count).AppendLine();
        builder.AppendLine("# HELP lywaf_process_handles Process handle count where supported.");
        builder.AppendLine("# TYPE lywaf_process_handles gauge");
        builder.Append("lywaf_process_handles ").Append(process.HandleCount).AppendLine();
    }

    private void AppendRequests(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_http_requests_total Completed proxy requests.");
        builder.AppendLine("# TYPE lywaf_http_requests_total counter");
        builder.AppendLine("# HELP lywaf_http_request_duration_seconds Proxy request duration.");
        builder.AppendLine("# TYPE lywaf_http_request_duration_seconds histogram");

        foreach (var (key, value) in _requests.OrderBy(static item => item.Key.Route, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Method, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Status, StringComparer.Ordinal))
        {
            var labels = $"method=\"{EscapeLabel(key.Method)}\",route=\"{EscapeLabel(key.Route)}\",code=\"{EscapeLabel(key.Status)}\"";
            var snapshot = value.Snapshot();
            builder.Append("lywaf_http_requests_total{").Append(labels).Append("} ").Append(snapshot.Count).AppendLine();

            long cumulative = 0;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                builder.Append("lywaf_http_request_duration_seconds_bucket{").Append(labels)
                    .Append(",le=\"").Append(DurationBuckets[i].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(cumulative).AppendLine();
            }
            builder.Append("lywaf_http_request_duration_seconds_bucket{").Append(labels)
                .Append(",le=\"+Inf\"} ").Append(snapshot.Count).AppendLine();
            builder.Append("lywaf_http_request_duration_seconds_sum{").Append(labels).Append("} ")
                .Append(snapshot.SumSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).AppendLine();
            builder.Append("lywaf_http_request_duration_seconds_count{").Append(labels).Append("} ")
                .Append(snapshot.Count).AppendLine();
        }

        builder.AppendLine("# HELP lywaf_http_request_body_bytes_total Known proxy request body bytes.");
        builder.AppendLine("# TYPE lywaf_http_request_body_bytes_total counter");
        foreach (var (route, value) in _requestBodyBytes.OrderBy(static item => item.Key, StringComparer.Ordinal))
            builder.Append("lywaf_http_request_body_bytes_total{route=\"").Append(EscapeLabel(route))
                .Append("\"} ").Append(value.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_http_response_body_bytes_total Actual proxy response body bytes written.");
        builder.AppendLine("# TYPE lywaf_http_response_body_bytes_total counter");
        foreach (var (route, value) in _responseBodyBytes.OrderBy(static item => item.Key, StringComparer.Ordinal))
            builder.Append("lywaf_http_response_body_bytes_total{route=\"").Append(EscapeLabel(route))
                .Append("\"} ").Append(value.Value).AppendLine();
    }

    private void AppendAdminRequests(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_admin_api_requests_total Completed Admin API requests.");
        builder.AppendLine("# TYPE lywaf_admin_api_requests_total counter");
        builder.AppendLine("# HELP lywaf_admin_api_request_duration_seconds Admin API request duration.");
        builder.AppendLine("# TYPE lywaf_admin_api_request_duration_seconds histogram");
        builder.AppendLine("# HELP lywaf_admin_api_requests_in_flight Admin API requests currently being processed.");
        builder.AppendLine("# TYPE lywaf_admin_api_requests_in_flight gauge");
        builder.Append("lywaf_admin_api_requests_in_flight ").Append(_adminInFlight.Value).AppendLine();
        foreach (var (key, value) in _adminRequests.OrderBy(static item => item.Key.Method, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Status, StringComparer.Ordinal))
        {
            var labels = $"method=\"{EscapeLabel(key.Method)}\",code=\"{EscapeLabel(key.Status)}\"";
            var snapshot = value.Snapshot();
            builder.Append("lywaf_admin_api_requests_total{").Append(labels).Append("} ").Append(snapshot.Count).AppendLine();
            long cumulative = 0;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                builder.Append("lywaf_admin_api_request_duration_seconds_bucket{").Append(labels)
                    .Append(",le=\"").Append(DurationBuckets[i].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(cumulative).AppendLine();
            }
            builder.Append("lywaf_admin_api_request_duration_seconds_bucket{").Append(labels)
                .Append(",le=\"+Inf\"} ").Append(snapshot.Count).AppendLine();
            builder.Append("lywaf_admin_api_request_duration_seconds_sum{").Append(labels).Append("} ")
                .Append(snapshot.SumSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).AppendLine();
            builder.Append("lywaf_admin_api_request_duration_seconds_count{").Append(labels).Append("} ")
                .Append(snapshot.Count).AppendLine();
        }
    }

    private void AppendInFlight(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_http_requests_in_flight Proxy requests currently being processed.");
        builder.AppendLine("# TYPE lywaf_http_requests_in_flight gauge");
        foreach (var (route, value) in _inFlight.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            builder.Append("lywaf_http_requests_in_flight{route=\"").Append(EscapeLabel(route))
                .Append("\"} ").Append(value.Value).AppendLine();
        }
    }

    private void AppendProxyErrors(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_proxy_errors_total Gateway-generated proxy errors.");
        builder.AppendLine("# TYPE lywaf_proxy_errors_total counter");
        foreach (var (reason, value) in _proxyErrors.OrderBy(static item => item.Key, StringComparer.Ordinal))
            builder.Append("lywaf_proxy_errors_total{reason=\"").Append(reason).Append("\"} ")
                .Append(value.Value).AppendLine();
    }

    private void AppendRetries(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_upstream_retry_attempts_total Upstream retry decisions and outcomes.");
        builder.AppendLine("# TYPE lywaf_upstream_retry_attempts_total counter");
        foreach (var (key, value) in _retryAttempts.OrderBy(static item => item.Key.Cluster, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Destination, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Outcome, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Attempt))
            builder.Append("lywaf_upstream_retry_attempts_total{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",destination=\"").Append(EscapeLabel(key.Destination))
                .Append("\",outcome=\"").Append(EscapeLabel(key.Outcome))
                .Append("\",attempt=\"").Append(key.Attempt).Append("\"} ").Append(value.Value).AppendLine();
    }

    private void AppendCircuitBreakers(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_circuit_breaker_state Circuit state: 0 closed, 1 saturated, 2 open.");
        builder.AppendLine("# TYPE lywaf_circuit_breaker_state gauge");
        builder.AppendLine("# HELP lywaf_circuit_breaker_capacity Current circuit capacity counters.");
        builder.AppendLine("# TYPE lywaf_circuit_breaker_capacity gauge");
        foreach (var (key, value) in _circuits.OrderBy(static item => item.Key.Cluster, StringComparer.Ordinal))
        {
            var state = value.State == "open" ? 2 : value.State == "saturated" ? 1 : 0;
            builder.Append("lywaf_circuit_breaker_state{cluster=\"").Append(EscapeLabel(key.Cluster)).Append("\"} ").Append(state).AppendLine();
            AppendCircuitCapacity(builder, key.Cluster, "remaining", value.Remaining);
            AppendCircuitCapacity(builder, key.Cluster, "queued", value.Queued);
            AppendCircuitCapacity(builder, key.Cluster, "active", value.Active);
            AppendCircuitCapacity(builder, key.Cluster, "retries", value.Retries);
        }
        builder.AppendLine("# HELP lywaf_circuit_breaker_rejections_total Requests rejected by circuit capacity limits.");
        builder.AppendLine("# TYPE lywaf_circuit_breaker_rejections_total counter");
        foreach (var (key, value) in _circuitRejections)
            builder.Append("lywaf_circuit_breaker_rejections_total{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",reason=\"").Append(EscapeLabel(key.Reason)).Append("\"} ").Append(value.Value).AppendLine();
    }

    private static void AppendCircuitCapacity(StringBuilder builder, string cluster, string kind, int value)
        => builder.Append("lywaf_circuit_breaker_capacity{cluster=\"").Append(EscapeLabel(cluster))
            .Append("\",kind=\"").Append(kind).Append("\"} ").Append(value).AppendLine();

    private void AppendOutliers(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_outlier_ejected Whether a destination is passively ejected.");
        builder.AppendLine("# TYPE lywaf_outlier_ejected gauge");
        builder.AppendLine("# HELP lywaf_outlier_recovery_timestamp_seconds Scheduled recovery time.");
        builder.AppendLine("# TYPE lywaf_outlier_recovery_timestamp_seconds gauge");
        foreach (var (key, value) in _outliers)
        {
            var isEjected = value.Ejected && value.RecoveryTime > DateTimeOffset.UtcNow;
            builder.Append("lywaf_outlier_ejected{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",destination=\"").Append(EscapeLabel(key.Destination))
                .Append("\",address=\"").Append(EscapeLabel(key.Address)).Append("\",reason=\"")
                .Append(EscapeLabel(value.Reason)).Append("\"} ").Append(isEjected ? 1 : 0).AppendLine();
            builder.Append("lywaf_outlier_recovery_timestamp_seconds{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",destination=\"").Append(EscapeLabel(key.Destination)).Append("\"} ")
                .Append(value.RecoveryTime.ToUnixTimeSeconds()).AppendLine();
        }
        builder.AppendLine("# HELP lywaf_outlier_decisions_total Passive outlier decisions.");
        builder.AppendLine("# TYPE lywaf_outlier_decisions_total counter");
        foreach (var (key, value) in _outlierDecisions)
            builder.Append("lywaf_outlier_decisions_total{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",destination=\"").Append(EscapeLabel(key.Destination)).Append("\",reason=\"")
                .Append(EscapeLabel(key.Reason)).Append("\"} ").Append(value.Value).AppendLine();
    }

    private void AppendResponseCache(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_response_cache_requests_total Response cache decisions.");
        builder.AppendLine("# TYPE lywaf_response_cache_requests_total counter");
        foreach (var (outcome, value) in _responseCacheResults.OrderBy(static item => item.Key, StringComparer.Ordinal))
            builder.Append("lywaf_response_cache_requests_total{outcome=\"").Append(outcome)
                .Append("\"} ").Append(value.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_response_cache_entries Current response cache entries.");
        builder.AppendLine("# TYPE lywaf_response_cache_entries gauge");
        builder.Append("lywaf_response_cache_entries ").Append(_responseCacheEntries.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_response_cache_bytes Current response cache body bytes on disk.");
        builder.AppendLine("# TYPE lywaf_response_cache_bytes gauge");
        builder.Append("lywaf_response_cache_bytes ").Append(_responseCacheBytes.Value).AppendLine();
    }

    private void AppendMirror(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_mirror_requests_total Request mirror queue and delivery outcomes.");
        builder.AppendLine("# TYPE lywaf_mirror_requests_total counter");
        foreach (var (outcome, value) in _mirrorResults.OrderBy(static item => item.Key, StringComparer.Ordinal))
            builder.Append("lywaf_mirror_requests_total{outcome=\"").Append(outcome)
                .Append("\"} ").Append(value.Value).AppendLine();
    }

    private void AppendExternalAuthorization(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_ext_auth_requests_total External authorization decisions and failures.");
        builder.AppendLine("# TYPE lywaf_ext_auth_requests_total counter");
        builder.AppendLine("# HELP lywaf_ext_auth_request_duration_seconds External authorization duration.");
        builder.AppendLine("# TYPE lywaf_ext_auth_request_duration_seconds histogram");
        foreach (var (outcome, value) in _externalAuthorization.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var snapshot = value.Snapshot();
            builder.Append("lywaf_ext_auth_requests_total{outcome=\"").Append(outcome).Append("\"} ")
                .Append(snapshot.Count).AppendLine();
            long cumulative = 0;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                builder.Append("lywaf_ext_auth_request_duration_seconds_bucket{outcome=\"").Append(outcome)
                    .Append("\",le=\"").Append(DurationBuckets[i].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(cumulative).AppendLine();
            }
            builder.Append("lywaf_ext_auth_request_duration_seconds_bucket{outcome=\"").Append(outcome)
                .Append("\",le=\"+Inf\"} ").Append(snapshot.Count).AppendLine();
            builder.Append("lywaf_ext_auth_request_duration_seconds_sum{outcome=\"").Append(outcome).Append("\"} ")
                .Append(snapshot.SumSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).AppendLine();
            builder.Append("lywaf_ext_auth_request_duration_seconds_count{outcome=\"").Append(outcome).Append("\"} ")
                .Append(snapshot.Count).AppendLine();
        }
    }

    private void AppendStream(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_stream_events_total Stream connection and admission events.");
        builder.AppendLine("# TYPE lywaf_stream_events_total counter");
        foreach (var (key, value) in _streamEvents.OrderBy(static item => item.Key.Listen, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Protocol, StringComparer.Ordinal).ThenBy(static item => item.Key.Outcome, StringComparer.Ordinal))
            builder.Append("lywaf_stream_events_total{listen=\"").Append(EscapeLabel(key.Listen))
                .Append("\",protocol=\"").Append(key.Protocol).Append("\",outcome=\"").Append(key.Outcome)
                .Append("\"} ").Append(value.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_stream_connections_active Active TCP Stream connections.");
        builder.AppendLine("# TYPE lywaf_stream_connections_active gauge");
        foreach (var (key, value) in _streamActive.OrderBy(static item => item.Key.Listen, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Protocol, StringComparer.Ordinal))
            builder.Append("lywaf_stream_connections_active{listen=\"").Append(EscapeLabel(key.Listen))
                .Append("\",protocol=\"").Append(key.Protocol).Append("\"} ").Append(value.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_stream_bytes_total Stream payload bytes by direction.");
        builder.AppendLine("# TYPE lywaf_stream_bytes_total counter");
        foreach (var (key, value) in _streamBytes.OrderBy(static item => item.Key.Listen, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Protocol, StringComparer.Ordinal).ThenBy(static item => item.Key.Direction, StringComparer.Ordinal))
            builder.Append("lywaf_stream_bytes_total{listen=\"").Append(EscapeLabel(key.Listen))
                .Append("\",protocol=\"").Append(key.Protocol).Append("\",direction=\"").Append(key.Direction)
                .Append("\"} ").Append(value.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_stream_udp_sessions Active UDP client sessions.");
        builder.AppendLine("# TYPE lywaf_stream_udp_sessions gauge");
        foreach (var (key, value) in _streamUdpSessions.OrderBy(static item => item.Key.Listen, StringComparer.Ordinal))
            builder.Append("lywaf_stream_udp_sessions{listen=\"").Append(EscapeLabel(key.Listen))
                .Append("\",protocol=\"udp\"} ").Append(value.Value).AppendLine();
    }

    private void AppendCertificates(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_certificate_expiry_timestamp_seconds Certificate expiry as a Unix timestamp.");
        builder.AppendLine("# TYPE lywaf_certificate_expiry_timestamp_seconds gauge");
        builder.AppendLine("# HELP lywaf_certificate_status Certificate status: 1 valid, 0 unknown/error, -1 revoked/expired.");
        builder.AppendLine("# TYPE lywaf_certificate_status gauge");
        foreach (var (domain, snapshot) in _certificates.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            builder.Append("lywaf_certificate_expiry_timestamp_seconds{domain=\"").Append(EscapeLabel(domain))
                .Append("\",source=\"").Append(EscapeLabel(snapshot.Source)).Append("\"} ").Append(snapshot.ExpiresAt).AppendLine();
            var value = snapshot.Status is "revoked" or "expired" ? -1 : snapshot.Status == "valid" ? 1 : 0;
            builder.Append("lywaf_certificate_status{domain=\"").Append(EscapeLabel(domain)).Append("\",status=\"")
                .Append(EscapeLabel(snapshot.Status)).Append("\"} ").Append(value).AppendLine();
        }
    }

    private static string NormalizeStreamProtocol(string protocol) => protocol.Equals("udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";

    private void AppendUpstreamHealth(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_upstream_health Last active health result: 1 healthy, 0 unhealthy, -1 unknown.");
        builder.AppendLine("# TYPE lywaf_upstream_health gauge");
        foreach (var (key, value) in _upstreamHealth.OrderBy(static item => item.Key.Cluster, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Destination, StringComparer.Ordinal))
        {
            builder.Append("lywaf_upstream_health{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",destination=\"").Append(EscapeLabel(key.Destination))
                .Append("\"} ").Append(value).AppendLine();
        }
        builder.AppendLine("# HELP lywaf_upstream_concurrent_requests Requests currently assigned to a destination.");
        builder.AppendLine("# TYPE lywaf_upstream_concurrent_requests gauge");
        foreach (var (key, value) in _upstreamConcurrentRequests.OrderBy(static item => item.Key.Cluster, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Destination, StringComparer.Ordinal))
        {
            builder.Append("lywaf_upstream_concurrent_requests{cluster=\"").Append(EscapeLabel(key.Cluster))
                .Append("\",destination=\"").Append(EscapeLabel(key.Destination))
                .Append("\"} ").Append(value).AppendLine();
        }
    }

    private void AppendCaptcha(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_captcha_events_total Captcha challenges and verification results.");
        builder.AppendLine("# TYPE lywaf_captcha_events_total counter");
        foreach (var (key, value) in _captchaEvents.OrderBy(static item => item.Key.Action, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Result, StringComparer.Ordinal))
            builder.Append("lywaf_captcha_events_total{action=\"").Append(key.Action)
                .Append("\",result=\"").Append(key.Result).Append("\"} ").Append(value.Value).AppendLine();
    }

    private static void AppendSecurityEvents(StringBuilder builder)
    {
        var snapshot = SharedData.Security.GetSnapshot();
        builder.AppendLine("# HELP lywaf_security_events_total Security events recorded by category.");
        builder.AppendLine("# TYPE lywaf_security_events_total counter");
        AppendSecurityEvent(builder, "waf", snapshot.WafInterceptCount);
        AppendSecurityEvent(builder, "blacklist", snapshot.BlacklistBlockCount);
        AppendSecurityEvent(builder, "cc", snapshot.CcAttackCount);
        AppendSecurityEvent(builder, "crawler", snapshot.CrawlerDetectCount);
        AppendSecurityEvent(builder, "geo", snapshot.GeoBlockCount);
        AppendSecurityEvent(builder, "rate_limit", snapshot.RateLimitCount);
        builder.AppendLine("# HELP lywaf_security_attack_ips Current unique attack source count.");
        builder.AppendLine("# TYPE lywaf_security_attack_ips gauge");
        builder.Append("lywaf_security_attack_ips ").Append(snapshot.UniqueAttackIps).AppendLine();
    }

    private static void AppendSecurityEvent(StringBuilder builder, string category, long value)
        => builder.Append("lywaf_security_events_total{category=\"").Append(category).Append("\"} ")
            .Append(value).AppendLine();

    private void AppendWebTamper(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_web_tamper_queue_depth Pending lazy checks.");
        builder.AppendLine("# TYPE lywaf_web_tamper_queue_depth gauge");
        builder.Append("lywaf_web_tamper_queue_depth ").Append(_webTamperQueueDepth.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_web_tamper_queue_dropped_total Checks dropped because the queue was full.");
        builder.AppendLine("# TYPE lywaf_web_tamper_queue_dropped_total counter");
        builder.Append("lywaf_web_tamper_queue_dropped_total ").Append(_webTamperQueueDropped.Value).AppendLine();
        builder.AppendLine("# HELP lywaf_web_tamper_checks_total Completed checks by outcome.");
        builder.AppendLine("# TYPE lywaf_web_tamper_checks_total counter");
        builder.AppendLine("# HELP lywaf_web_tamper_check_duration_seconds Web tamper check duration.");
        builder.AppendLine("# TYPE lywaf_web_tamper_check_duration_seconds histogram");
        foreach (var (outcome, value) in _webTamperChecks.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var snapshot = value.Snapshot();
            builder.Append("lywaf_web_tamper_checks_total{outcome=\"").Append(outcome).Append("\"} ")
                .Append(snapshot.Count).AppendLine();
            long cumulative = 0;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                builder.Append("lywaf_web_tamper_check_duration_seconds_bucket{outcome=\"").Append(outcome)
                    .Append("\",le=\"").Append(DurationBuckets[i].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(cumulative).AppendLine();
            }
            builder.Append("lywaf_web_tamper_check_duration_seconds_bucket{outcome=\"").Append(outcome)
                .Append("\",le=\"+Inf\"} ").Append(snapshot.Count).AppendLine();
            builder.Append("lywaf_web_tamper_check_duration_seconds_sum{outcome=\"").Append(outcome).Append("\"} ")
                .Append(snapshot.SumSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).AppendLine();
            builder.Append("lywaf_web_tamper_check_duration_seconds_count{outcome=\"").Append(outcome).Append("\"} ")
                .Append(snapshot.Count).AppendLine();
        }
    }

    private void AppendDuckDb(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_duckdb_enabled Whether DuckDB persistence is currently available.");
        builder.AppendLine("# TYPE lywaf_duckdb_enabled gauge");
        builder.Append("lywaf_duckdb_enabled ").Append(Volatile.Read(ref _duckDbEnabled)).AppendLine();
        builder.AppendLine("# HELP lywaf_duckdb_operations_total DuckDB operations by type and result.");
        builder.AppendLine("# TYPE lywaf_duckdb_operations_total counter");
        builder.AppendLine("# HELP lywaf_duckdb_operation_duration_seconds DuckDB operation duration.");
        builder.AppendLine("# TYPE lywaf_duckdb_operation_duration_seconds histogram");
        foreach (var (key, value) in _databaseOperations.OrderBy(static item => item.Key.Operation, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Success))
        {
            var result = key.Success ? "success" : "failure";
            var labels = $"operation=\"{key.Operation}\",result=\"{result}\"";
            var snapshot = value.Snapshot();
            builder.Append("lywaf_duckdb_operations_total{").Append(labels).Append("} ")
                .Append(snapshot.Count).AppendLine();
            long cumulative = 0;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += snapshot.Buckets[i];
                builder.Append("lywaf_duckdb_operation_duration_seconds_bucket{").Append(labels)
                    .Append(",le=\"").Append(DurationBuckets[i].ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("\"} ").Append(cumulative).AppendLine();
            }
            builder.Append("lywaf_duckdb_operation_duration_seconds_bucket{").Append(labels)
                .Append(",le=\"+Inf\"} ").Append(snapshot.Count).AppendLine();
            builder.Append("lywaf_duckdb_operation_duration_seconds_sum{").Append(labels).Append("} ")
                .Append(snapshot.SumSeconds.ToString("0.000000", CultureInfo.InvariantCulture)).AppendLine();
            builder.Append("lywaf_duckdb_operation_duration_seconds_count{").Append(labels).Append("} ")
                .Append(snapshot.Count).AppendLine();
        }
        builder.AppendLine("# HELP lywaf_duckdb_retention_deleted_rows_total Rows deleted by retention policy.");
        builder.AppendLine("# TYPE lywaf_duckdb_retention_deleted_rows_total counter");
        foreach (var (table, value) in _retentionDeletedRows.OrderBy(static item => item.Key, StringComparer.Ordinal))
            builder.Append("lywaf_duckdb_retention_deleted_rows_total{table=\"").Append(table).Append("\"} ")
                .Append(value.Value).AppendLine();
    }

    private static void AppendPluginState(StringBuilder builder, IPluginManager pluginManager)
    {
        builder.AppendLine("# HELP lywaf_plugin_state Current plugin lifecycle state.");
        builder.AppendLine("# TYPE lywaf_plugin_state gauge");
        foreach (var plugin in pluginManager.GetAllPlugins().OrderBy(static item => item.Metadata.Id, StringComparer.Ordinal))
        {
            builder.Append("lywaf_plugin_state{plugin=\"").Append(EscapeLabel(plugin.Metadata.Id))
                .Append("\",state=\"").Append(EscapeLabel(plugin.State.ToString().ToLowerInvariant()))
                .AppendLine("\"} 1");
        }
    }

    private void AppendPluginOperations(StringBuilder builder)
    {
        builder.AppendLine("# HELP lywaf_plugin_operations_total Plugin lifecycle operations by result.");
        builder.AppendLine("# TYPE lywaf_plugin_operations_total counter");
        foreach (var (key, value) in _pluginOperations.OrderBy(static item => item.Key.Plugin, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Operation, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Success))
            builder.Append("lywaf_plugin_operations_total{plugin=\"").Append(EscapeLabel(key.Plugin))
                .Append("\",operation=\"").Append(key.Operation).Append("\",result=\"")
                .Append(key.Success ? "success" : "failure").Append("\"} ").Append(value.Value).AppendLine();
    }

    /// <summary>确保一个代理请求只完成一次，并在异常退出时归还在途计数。</summary>
    public sealed class RequestObservation(PrometheusMetrics owner, string route, long startedAt) : IDisposable
    {
        private int _completed;

        public void Complete(string? method, int statusCode, long requestBytes = 0, long responseBytes = 0)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                owner.Complete(route, method, statusCode, requestBytes, responseBytes, startedAt);
        }

        public void Dispose() => Complete(null, 0);
    }

    /// <summary>确保一个 Admin API 请求只完成一次，并在异常退出时归还在途计数。</summary>
    public sealed class AdminRequestObservation(PrometheusMetrics owner, long startedAt) : IDisposable
    {
        private int _completed;

        public void Complete(string? method, int statusCode)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                owner.CompleteAdmin(method, statusCode, startedAt);
        }

        public void Dispose() => Complete(null, 0);
    }

    private sealed class AtomicLong
    {
        private long _value;
        public long Value => Interlocked.Read(ref _value);
        public void Increment() => Interlocked.Increment(ref _value);
        public void Add(long value) => Interlocked.Add(ref _value, value);
        public void Set(long value) => Interlocked.Exchange(ref _value, value);
        public void DecrementFloorZero()
        {
            while (true)
            {
                var current = Interlocked.Read(ref _value);
                if (current <= 0 || Interlocked.CompareExchange(ref _value, current - 1, current) == current) return;
            }
        }
    }

    private sealed class RequestSeries(int bucketCount)
    {
        private readonly long[] _buckets = new long[bucketCount];
        private long _count;
        private long _sumNanoseconds;

        public void Observe(double seconds, IReadOnlyList<double> buckets)
        {
            Interlocked.Increment(ref _count);
            var nanoseconds = seconds >= long.MaxValue / 1_000_000_000d
                ? long.MaxValue
                : (long)(seconds * 1_000_000_000d);
            Interlocked.Add(ref _sumNanoseconds, nanoseconds);
            for (var i = 0; i < buckets.Count; i++)
            {
                if (seconds <= buckets[i])
                {
                    Interlocked.Increment(ref _buckets[i]);
                    break;
                }
            }
        }

        public RequestSnapshot Snapshot()
        {
            var buckets = new long[_buckets.Length];
            for (var i = 0; i < buckets.Length; i++) buckets[i] = Interlocked.Read(ref _buckets[i]);
            return new RequestSnapshot(
                Interlocked.Read(ref _count),
                Interlocked.Read(ref _sumNanoseconds) / 1_000_000_000d,
                buckets);
        }
    }

    private readonly record struct RequestKey(string Route, string Method, string Status);
    private readonly record struct AdminRequestKey(string Method, string Status);
    private readonly record struct UpstreamKey(string Cluster, string Destination);
    private readonly record struct DatabaseKey(string Operation, bool Success);
    private readonly record struct CaptchaKey(string Action, string Result);
    private readonly record struct PluginOperationKey(string Plugin, string Operation, bool Success);
    private readonly record struct RetryKey(string Cluster, string Destination, string Outcome, int Attempt);
    private readonly record struct CircuitKey(string Cluster);
    private readonly record struct CircuitRejectKey(string Cluster, string Reason);
    private readonly record struct CircuitSnapshot(string State, int Remaining, int Queued, int Active, int Retries);
    private readonly record struct OutlierKey(string Cluster, string Destination, string Address);
    private readonly record struct OutlierDecisionKey(string Cluster, string Destination, string Reason);
    private readonly record struct StreamKey(string Listen, string Protocol);
    private readonly record struct StreamEventKey(string Listen, string Protocol, string Outcome);
    private readonly record struct StreamByteKey(string Listen, string Protocol, string Direction);
    private readonly record struct CertificateMetricSnapshot(long ExpiresAt, string Source, string Status);
    private readonly record struct OutlierMetricSnapshot(bool Ejected, string Reason, DateTimeOffset RecoveryTime);
    private readonly record struct RequestSnapshot(long Count, double SumSeconds, long[] Buckets);
}
