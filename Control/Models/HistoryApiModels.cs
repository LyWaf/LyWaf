using System.Globalization;
using LyWaf.Services.DuckDb;
using LyWaf.Shared;

namespace LyWaf.Control;

/// <summary>历史图表的响应模型，与逐条资源管理的分页列表区分。</summary>
public sealed record TrafficHistoryResponse(bool Success, TrafficSnapshotRow[] Data, DateTime From, DateTime To, string Granularity);
public sealed record SecurityHistoryResponse(bool Success, SecurityTimeSlot[] Data, DateTime From, DateTime To);
public sealed record TimingHistoryResponse(bool Success, TimingHistoryPoint[] Data, DateTime From, DateTime To);
public sealed record TimingHistoryPoint(DateTime SnapshotTime, string Path, string Method, string Backend,
    string OriginalHost, int RequestCount, double AvgTotalTime, double AvgBackendTime, long MinTotalTime,
    long MaxTotalTime, Dictionary<int, int> StatusCodeCounts);
public sealed record EndpointHistoryResponse(bool Success, EndpointStatRow[] Data, DateTime From, DateTime To, string StatType);
public sealed record RealtimeHistoryResponse<T>(bool Success, T[] Data, string Granularity, string Source);

/// <summary>历史图表统一校验 UTC 时间范围和枚举参数，防止无效值静默扩大查询。</summary>
internal static class HistoryQuery
{
    public static (DateTime From, DateTime To) Range(IQueryCollection query, TimeSpan defaultRange)
    {
        var now = DateTime.UtcNow;
        var from = ReadTime(query, "from") ?? now.Subtract(defaultRange);
        var to = ReadTime(query, "to") ?? now;
        if (from > to) throw new ArgumentException("结束时间不得早于开始时间", "to");
        if (to - from > TimeSpan.FromDays(366)) throw new ArgumentException("单次查询时间范围不得超过 366 天", "from");
        return (from, to);
    }

    public static string Choice(IQueryCollection query, string name, string fallback, params string[] allowed)
    {
        var value = Value(query, name) ?? fallback;
        if (!allowed.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("不支持的查询选项", name);
        return value;
    }

    private static DateTime? ReadTime(IQueryCollection query, string name)
    {
        var value = Value(query, name);
        if (value == null) return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            throw new ArgumentException("时间格式无效", name);
        return time.UtcDateTime;
    }

    private static string? Value(IQueryCollection query, string name)
    {
        if (!query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 128)
            throw new ArgumentException("查询参数无效", name);
        return values[0];
    }
}
