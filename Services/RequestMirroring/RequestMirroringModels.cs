using System.Globalization;

namespace LyWaf.Services.RequestMirroring;

/// <summary>请求镜像后台执行器的进程级资源限制。</summary>
public sealed class RequestMirroringOptions
{
    /// <summary>等待发送的镜像请求最大数量；队列满时跳过镜像，不阻塞主请求。</summary>
    public int QueueCapacity { get; set; } = 256;

    /// <summary>并行发送镜像请求的固定后台 worker 数量。</summary>
    public int Workers { get; set; } = 2;

    /// <summary>单个镜像请求的超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// 从 Route Metadata 读取的不可变镜像策略。策略默认关闭，并且仅允许安全、可重放的方法。
/// </summary>
public sealed record RequestMirrorPolicy(
    bool Enabled,
    Uri? Target,
    double Percentage,
    long MaxBodyBytes,
    IReadOnlySet<string> Methods,
    IReadOnlySet<string> AllowedHeaders)
{
    public static RequestMirrorPolicy Disabled { get; } = new(false, null, 0, 0,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>解析 `Mirror.*` Route Metadata；无效目标或未启用配置返回关闭策略。</summary>
    public static RequestMirrorPolicy FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null || !ReadBool(metadata, "Mirror.Enabled", false)) return Disabled;
        if (!metadata.TryGetValue("Mirror.Target", out var targetValue) ||
            !Uri.TryCreate(targetValue, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
            return Disabled;
        var percentage = Math.Clamp(ReadDouble(metadata, "Mirror.Percentage", 100), 0, 100);
        var maxBody = Math.Clamp(ReadLong(metadata, "Mirror.MaxBodyBytes", 1_048_576), 0, 16L * 1024 * 1024);
        return new RequestMirrorPolicy(percentage > 0, target, percentage, maxBody,
            ReadList(metadata, "Mirror.Methods", ["GET", "HEAD", "OPTIONS"]),
            ReadList(metadata, "Mirror.AllowedHeaders", ["Accept", "Content-Type", "User-Agent"]));
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static long ReadLong(IReadOnlyDictionary<string, string> values, string key, long fallback) =>
        values.TryGetValue(key, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static IReadOnlySet<string> ReadList(IReadOnlyDictionary<string, string> values, string key, string[] fallback) =>
        (values.TryGetValue(key, out var value) ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : fallback)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>已脱离入口请求生命周期、可由后台 worker 安全发送的镜像请求快照。</summary>
public sealed record MirroredRequest(HttpMethod Method, Uri Target, IReadOnlyDictionary<string, string[]> Headers,
    byte[]? Body, string? ContentType);
