using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;

namespace LyWaf.Services.Telemetry;

/// <summary>集中定义 LyWaf 的 ActivitySource 与 Meter，供链路追踪和运行指标共享。</summary>
public static class LyWafTelemetry
{
    public const string SourceName = "LyWaf";
    public const string MeterName = "LyWaf";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> ProxyRequests = Meter.CreateCounter<long>("lywaf.http.requests", "{request}");
    public static readonly Histogram<double> ProxyDuration = Meter.CreateHistogram<double>("lywaf.http.request.duration", "s");
    public static readonly UpDownCounter<long> ProxyInFlight = Meter.CreateUpDownCounter<long>("lywaf.http.requests.active", "{request}");
    public static readonly Counter<long> ProxyRequestBytes = Meter.CreateCounter<long>("lywaf.http.request.body.size", "By");
    public static readonly Counter<long> ProxyResponseBytes = Meter.CreateCounter<long>("lywaf.http.response.body.size", "By");
    public static readonly Counter<long> AdminRequests = Meter.CreateCounter<long>("lywaf.admin.requests", "{request}");
    public static readonly Histogram<double> AdminDuration = Meter.CreateHistogram<double>("lywaf.admin.request.duration", "s");
    public static readonly UpDownCounter<long> AdminInFlight = Meter.CreateUpDownCounter<long>("lywaf.admin.requests.active", "{request}");
    public static readonly Counter<long> MirroredRequests = Meter.CreateCounter<long>("lywaf.mirror.requests", "{request}");
    public static readonly Counter<long> ExternalAuthorizationRequests = Meter.CreateCounter<long>("lywaf.ext_auth.requests", "{request}");
    public static readonly Histogram<double> ExternalAuthorizationDuration = Meter.CreateHistogram<double>("lywaf.ext_auth.request.duration", "s");
    private static readonly ConcurrentDictionary<(string Cluster, string Destination), int> UpstreamHealth = new();
    private static readonly ConcurrentDictionary<(string Cluster, string Destination), int> UpstreamConcurrency = new();

    static LyWafTelemetry()
    {
        Meter.CreateObservableGauge("lywaf.upstream.health", ObserveUpstreamHealth,
            description: "Last active health result: 1 healthy, 0 unhealthy, -1 unknown.");
        Meter.CreateObservableGauge("lywaf.upstream.requests.active", ObserveUpstreamConcurrency,
            unit: "{request}", description: "Requests currently assigned to an upstream destination.");
    }

    /// <summary>更新 OTLP 导出的上游健康状态。</summary>
    public static void SetUpstreamHealth(string cluster, string destination, int health) =>
        UpstreamHealth[(cluster, destination)] = health;

    /// <summary>更新 OTLP 导出的上游当前请求数。</summary>
    public static void SetUpstreamConcurrency(string cluster, string destination, int count) =>
        UpstreamConcurrency[(cluster, destination)] = count;

    private static IEnumerable<Measurement<int>> ObserveUpstreamHealth() => UpstreamHealth.Select(static item =>
        new Measurement<int>(item.Value, new KeyValuePair<string, object?>("cluster", item.Key.Cluster),
            new KeyValuePair<string, object?>("destination", item.Key.Destination)));

    private static IEnumerable<Measurement<int>> ObserveUpstreamConcurrency() => UpstreamConcurrency.Select(static item =>
        new Measurement<int>(item.Value, new KeyValuePair<string, object?>("cluster", item.Key.Cluster),
            new KeyValuePair<string, object?>("destination", item.Key.Destination)));
}
