using System.Globalization;

namespace LyWaf.Services.ExternalAuthorization;

/// <summary>外部授权服务的全局连接、载荷和缓存边界。</summary>
public sealed class ExternalAuthorizationOptions
{
    /// <summary>默认授权服务 HTTP/HTTPS 地址；Route Metadata 可覆盖。</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>授权调用硬超时毫秒数。</summary>
    public int TimeoutMilliseconds { get; set; } = 1000;
    /// <summary>发送给授权服务的最大请求正文前缀字节数。</summary>
    public int MaxBodyBytes { get; set; } = 8192;
    /// <summary>授权响应 JSON 的最大字节数。</summary>
    public int MaxResponseBytes { get; set; } = 65536;
    /// <summary>发送给授权服务的 Header 名称和值累计最大字节数。</summary>
    public int MaxHeaderBytes { get; set; } = 16384;
    /// <summary>授权服务失败时是否允许请求继续；安全默认值为 false。</summary>
    public bool FailOpen { get; set; }
    /// <summary>允许发送给授权服务的请求 Header 名称。</summary>
    public string[] AllowedRequestHeaders { get; set; } = ["Authorization", "Cookie", "Content-Type"];
    /// <summary>允许授权服务写入原请求、供上游读取的 Header 名称。</summary>
    public string[] AllowedUpstreamHeaders { get; set; } = ["X-Authenticated-User", "X-Authenticated-Groups"];
    /// <summary>授权结果默认缓存秒数；0 表示关闭缓存。</summary>
    public int CacheSeconds { get; set; }
    /// <summary>授权缓存最大条目数。</summary>
    public int MaxCacheEntries { get; set; } = 10000;
    /// <summary>参与用户隔离缓存键的 Header；值只计算 SHA-256，不以明文保存。</summary>
    public string[] CacheKeyHeaders { get; set; } = ["Authorization"];
}

/// <summary>从 Route Metadata 与全局配置合并得到的单次授权策略。</summary>
public sealed record ExternalAuthorizationPolicy(bool Enabled, Uri? Endpoint, int TimeoutMilliseconds,
    int MaxBodyBytes, bool FailOpen, int CacheSeconds)
{
    public static ExternalAuthorizationPolicy FromMetadata(IReadOnlyDictionary<string, string>? metadata,
        ExternalAuthorizationOptions defaults)
    {
        var enabled = ReadBool(metadata, "ExtAuth.Enabled", false);
        var endpointValue = Read(metadata, "ExtAuth.Endpoint") ?? defaults.Endpoint;
        var validEndpoint = Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) &&
                            endpoint.Scheme is "http" or "https";
        return new(enabled, validEndpoint ? endpoint : null,
            Math.Clamp(ReadInt(metadata, "ExtAuth.TimeoutMilliseconds", defaults.TimeoutMilliseconds), 50, 30_000),
            Math.Clamp(ReadInt(metadata, "ExtAuth.MaxBodyBytes", defaults.MaxBodyBytes), 0, 1_048_576),
            ReadBool(metadata, "ExtAuth.FailOpen", defaults.FailOpen),
            Math.Clamp(ReadInt(metadata, "ExtAuth.CacheSeconds", defaults.CacheSeconds), 0, 3600));
    }

    private static string? Read(IReadOnlyDictionary<string, string>? values, string key) =>
        values?.TryGetValue(key, out var value) == true ? value : null;
    private static bool ReadBool(IReadOnlyDictionary<string, string>? values, string key, bool fallback) =>
        bool.TryParse(Read(values, key), out var value) ? value : fallback;
    private static int ReadInt(IReadOnlyDictionary<string, string>? values, string key, int fallback) =>
        int.TryParse(Read(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

/// <summary>发送给外部授权服务的最小请求视图。</summary>
public sealed record ExternalAuthorizationRequest(string Method, string Scheme, string Host, string Path,
    IReadOnlyDictionary<string, string[]> Headers, string? BodyBase64, bool BodyTruncated);

/// <summary>外部授权服务返回的决策；Headers 仅在全局允许列表中时写入上游请求。</summary>
public sealed record ExternalAuthorizationResponse(bool Allowed, int StatusCode = 403, string? Reason = null,
    Dictionary<string, string>? Headers = null, int? CacheSeconds = null);

/// <summary>供短期缓存保存的授权决策，不包含原始凭据或请求正文。</summary>
internal sealed record CachedAuthorization(ExternalAuthorizationResponse Response, DateTimeOffset ExpiresAt);
