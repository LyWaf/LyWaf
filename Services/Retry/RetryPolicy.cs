using System.Globalization;

namespace LyWaf.Services.Retry;

/// <summary>
/// 上游请求重试策略，由 Route 或 Cluster Metadata 构造并在转发请求期间保持不可变。
/// </summary>
public sealed record RetryPolicy(
    bool Enabled,
    int MaxAttempts,
    IReadOnlySet<string> Methods,
    IReadOnlySet<int> StatusCodes,
    bool RetryNetworkErrors,
    int PerTryTimeoutMs,
    int TotalTimeoutMs,
    int BaseBackoffMs,
    int MaxBackoffMs,
    int BudgetPercent,
    int BudgetMinRetriesPerSecond)
{
    public static RetryPolicy Disabled { get; } = new(
        false, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<int>(),
        false, 0, 0, 0, 0, 0, 0);

    public static RetryPolicy FromMetadata(IReadOnlyDictionary<string, string>? metadata, RetryPolicy? fallback = null)
    {
        fallback ??= Disabled;
        if (metadata == null) return fallback;

        var prefix = metadata.Keys.Any(static key => key.StartsWith("Retry.", StringComparison.OrdinalIgnoreCase))
            ? "Retry." : metadata.Keys.Any(static key => key.StartsWith("_Retry.", StringComparison.OrdinalIgnoreCase))
                ? "_Retry." : null;
        if (prefix == null) return fallback;

        var enabled = ReadBool(metadata, prefix + "Enabled", true);
        var attempts = Math.Clamp(ReadInt(metadata, prefix + "MaxAttempts", fallback.MaxAttempts > 1 ? fallback.MaxAttempts : 3), 1, 10);
        var methods = ReadStrings(metadata, prefix + "Methods", ["GET", "HEAD", "OPTIONS"])
            .Select(static value => value.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codes = ReadStrings(metadata, prefix + "StatusCodes", ["502", "503", "504"])
            .Select(static value => int.TryParse(value, CultureInfo.InvariantCulture, out var code) ? code : 0)
            .Where(static code => code is >= 100 and <= 599).ToHashSet();

        return new RetryPolicy(
            enabled && attempts > 1,
            attempts,
            methods,
            codes,
            ReadBool(metadata, prefix + "RetryNetworkErrors", true),
            Math.Clamp(ReadInt(metadata, prefix + "PerTryTimeoutMs", 5000), 100, 300_000),
            Math.Clamp(ReadInt(metadata, prefix + "TotalTimeoutMs", 15000), 100, 600_000),
            Math.Clamp(ReadInt(metadata, prefix + "BaseBackoffMs", 100), 0, 60_000),
            Math.Clamp(ReadInt(metadata, prefix + "MaxBackoffMs", 2000), 0, 60_000),
            Math.Clamp(ReadInt(metadata, prefix + "BudgetPercent", 20), 0, 100),
            Math.Clamp(ReadInt(metadata, prefix + "BudgetMinRetriesPerSecond", 1), 0, 1000));
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static IEnumerable<string> ReadStrings(IReadOnlyDictionary<string, string> values, string key, string[] fallback)
        => values.TryGetValue(key, out var value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : fallback;
}

/// <summary>
/// 定义附加到 <see cref="HttpRequestMessage"/> 的 Route 级重试策略键。
/// </summary>
public static class RetryRequestOptions
{
    public static readonly HttpRequestOptionsKey<RetryPolicy> Policy = new("LyWaf.RetryPolicy");
}
