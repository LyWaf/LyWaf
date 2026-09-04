using System.Diagnostics;
using LyWaf.Services.DuckDb;
using LyWaf.Shared;
using LyWaf.Services.Telemetry;

namespace LyWaf.Services.Metrics;

public sealed class MetricsDuckDbQueryService(
    DuckDbQueryService inner,
    PrometheusMetrics metrics) : IDuckDbQueryService
{
    public bool IsEnabled => inner.IsEnabled;

    public List<TrafficSnapshotRow> GetTrafficHistory(DateTime from, DateTime to, string granularity = "hour")
        => Observe(() => inner.GetTrafficHistory(from, to, granularity));

    public List<SecurityTimeSlot> GetSecurityTimeSlots(DateTime from, DateTime to)
        => Observe(() => inner.GetSecurityTimeSlots(from, to));

    public List<ApiTimingRow> GetApiTimingHistory(DateTime from, DateTime to, string? pathFilter = null)
        => Observe(() => inner.GetApiTimingHistory(from, to, pathFilter));

    public GeoTrafficSnapshot GetGeoTrafficHistory()
        => Observe(inner.GetGeoTrafficHistory);

    public (List<InterceptEventRow> Items, int Total) GetInterceptEventsPaged(
        string? ipFilter, string? domainFilter, DateTime? start, DateTime? end, int offset, int limit)
        => Observe(() => inner.GetInterceptEventsPaged(ipFilter, domainFilter, start, end, offset, limit));

    public (List<AuditLogRow> Items, int Total) GetAuditLogsPaged(int offset, int limit)
        => Observe(() => inner.GetAuditLogsPaged(offset, limit));

    public List<EndpointStatRow> GetEndpointStatsHistory(string statType, DateTime from, DateTime to, int topN = 50)
        => Observe(() => inner.GetEndpointStatsHistory(statType, from, to, topN));

    public List<QpsSnapshotRow> GetQpsHistory(DateTime from, DateTime to, string granularity = "1min")
        => Observe(() => inner.GetQpsHistory(from, to, granularity));

    public List<BandwidthSnapshotRow> GetBandwidthHistory(DateTime from, DateTime to, string granularity = "1min")
        => Observe(() => inner.GetBandwidthHistory(from, to, granularity));

    private T Observe<T>(Func<T> action)
    {
        using var activity = LyWafTelemetry.ActivitySource.StartActivity("duckdb.query");
        activity?.SetTag("db.system", "duckdb");
        var startedAt = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var result = action();
            success = true;
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            metrics.ObserveDuckDbOperation("query", success, Stopwatch.GetElapsedTime(startedAt));
        }
    }
}
