using Microsoft.AspNetCore.Http;

namespace LyWaf.Services.ResponseCaching;

/// <summary>
/// 通用反向代理响应缓存配置。元数据只保存在进程内存，响应正文单独保存在 <see cref="Directory"/>。
/// 默认关闭，既可全局启用，也可通过 Route Metadata 的 <c>ResponseCache.Enabled</c> 逐路由启用。
/// </summary>
public sealed class ResponseCacheOptions
{
    /// <summary>没有路由级覆盖时是否启用缓存。</summary>
    public bool Enabled { get; set; }
    /// <summary>缓存正文目录。</summary>
    public string Directory { get; set; } = "datas/response-cache";
    /// <summary>可缓存单个响应的最大字节数；超过后立即切换为直接流式输出。</summary>
    public long MaxBodyBytes { get; set; } = 32 * 1024 * 1024;
    /// <summary>缓存正文总容量上限，按最近访问时间淘汰。</summary>
    public long MaxCacheBytes { get; set; } = 1024L * 1024 * 1024;
    /// <summary>上游未提供缓存时间时采用的默认 TTL。</summary>
    public int DefaultTtlSeconds { get; set; } = 60;
    /// <summary>过期后允许立即返回旧内容并异步刷新的时间。</summary>
    public int StaleWhileRevalidateSeconds { get; set; } = 30;
    /// <summary>回源异常或 5xx 时允许返回旧内容的时间。</summary>
    public int StaleIfErrorSeconds { get; set; } = 300;
    /// <summary>是否允许带 Authorization 请求进入缓存；默认关闭以防用户数据串用。</summary>
    public bool CacheAuthenticated { get; set; }
    /// <summary>允许缓存的请求方法。HEAD 与 GET 共用正文。</summary>
    public List<string> Methods { get; set; } = ["GET", "HEAD"];
    /// <summary>允许缓存的响应状态码。</summary>
    public List<int> StatusCodes { get; set; } = [200, 203, 204, 301, 404];
}

/// <summary>缓存条目的只读诊断快照，不包含正文，供管理 API 和测试查看。</summary>
public sealed record ResponseCacheEntrySnapshot(
    /// <summary>基于 Host、Path、Query 和 Vary 请求头生成的稳定 SHA-256 Key。</summary>
    string Key,
    /// <summary>请求 Host，包含非标准端口。</summary>
    string Host,
    /// <summary>请求路径及查询字符串。</summary>
    string Path,
    /// <summary>缓存的 HTTP 状态码。</summary>
    int StatusCode,
    /// <summary>磁盘正文长度。</summary>
    long Length,
    /// <summary>条目写入时间。</summary>
    DateTimeOffset StoredAt,
    /// <summary>正常有效期截止时间。</summary>
    DateTimeOffset FreshUntil,
    /// <summary>最近一次读取时间，用于容量淘汰。</summary>
    DateTimeOffset LastAccessedAt);

/// <summary>按 Key 或 Host/Path 前缀清理缓存的管理请求；条件为空时拒绝执行，防止误清空。</summary>
public sealed record PurgeResponseCacheRequest(string? Key = null, string? Host = null, string? Path = null);

internal sealed record ResponseCacheEntry(
    string Key, string BaseKey, string Host, string Path, int StatusCode, string BodyPath, long Length,
    DateTimeOffset StoredAt, DateTimeOffset FreshUntil, DateTimeOffset StaleWhileRevalidateUntil,
    DateTimeOffset StaleIfErrorUntil, IReadOnlyDictionary<string, string[]> Headers,
    IReadOnlyList<string> VaryHeaders, IReadOnlyDictionary<string, string> VaryValues,
    string? ETag, DateTimeOffset? LastModified)
{
    private long _lastAccessedTicks = StoredAt.UtcTicks;
    public DateTimeOffset LastAccessedAt => new(Interlocked.Read(ref _lastAccessedTicks), TimeSpan.Zero);
    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastAccessedTicks, now.UtcTicks);
    public ResponseCacheEntrySnapshot Snapshot() => new(Key, Host, Path, StatusCode, Length, StoredAt, FreshUntil, LastAccessedAt);
}

internal sealed record ResponseCachePolicy(
    bool Enabled, long MaxBodyBytes, TimeSpan DefaultTtl, TimeSpan StaleWhileRevalidate,
    TimeSpan StaleIfError, IReadOnlySet<string> Methods, IReadOnlySet<int> StatusCodes,
    bool CacheAuthenticated);

