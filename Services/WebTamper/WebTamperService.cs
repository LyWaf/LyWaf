using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Threading.Channels;
using NLog;
using LyWaf.Services.Metrics;
using LyWaf.Services.RuntimeState;

namespace LyWaf.Services.WebTamper;

/// <summary>一个受保护资源或目录规则，以及其本地安全基线和最近检查状态。</summary>
public sealed class WebTamperResource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Website { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ContentType { get; set; } = "text/html; charset=utf-8";
    public string BaselineHash { get; set; } = "";
    public string? CurrentHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedAt { get; set; }
    public bool Changed { get; set; }
    public bool IsPattern { get; set; }
    public string? ParentResourceId { get; set; }
    public int CapturedCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WebTamperResource>? CapturedResources { get; set; }
}

/// <summary>防篡改插件管理的网站维度，Origin 只保存域名及可选非标准端口。</summary>
public sealed class WebTamperWebsite
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>资源内容指纹发生变化后生成的事件；Resolved 表示变化已恢复或已被接受为新基线。</summary>
public sealed class WebTamperEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ResourceId { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string Url { get; set; } = "";
    public string BaselineHash { get; set; } = "";
    public string CurrentHash { get; set; } = "";
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public bool Resolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>中间件可直接返回的磁盘基线句柄和响应元数据，正文不会被完整加载到内存。</summary>
public sealed record ProtectedContent(string FilePath, string ContentType, long Length);
/// <summary>单次检查的资源状态及用于控制台差异视图的基线、线上内容预览。</summary>
public sealed record ContentComparison(WebTamperResource Resource, bool Changed, string BaselineContent, string CurrentContent);

/// <summary>网页防篡改插件的站点、资源、检查、基线接受和事件查询契约。</summary>
public interface IWebTamperService
{
    IReadOnlyList<WebTamperResource> GetResources();
    IReadOnlyList<WebTamperEvent> GetEvents();
    IReadOnlyList<WebTamperWebsite> GetWebsites();
    WebTamperWebsite AddWebsite(string name, string origin, int? port = null);
    WebTamperWebsite RenameWebsite(string name, string origin, string source);
    bool DeleteWebsite(string id);
    Task<WebTamperResource> AddAsync(string website, string path, CancellationToken cancellationToken);
    bool Update(string id, string name, bool enabled);
    bool Delete(string id);
    bool IsInternalCheck(HttpRequest request);
    bool TryGetProtectedContent(HttpRequest request, out WebTamperResource? resource, out ProtectedContent? baseline);
    void RecordObservedContent(WebTamperResource resource, string requestPath, string contentType, string contentFilePath, string currentHash);
    bool TryQueueBackgroundCheck(string website, string requestPath);
    Task ProcessBackgroundChecksAsync(CancellationToken cancellationToken);
    Task<ContentComparison?> CheckAsync(string id, CancellationToken cancellationToken);
    Task<ContentComparison?> AcceptCurrentAsync(string id, CancellationToken cancellationToken);
    Task CheckAllAsync(CancellationToken cancellationToken);
}

/// <summary>以文件句柄流式抓取内容、维护磁盘基线并把变更事件同步到运行状态层。</summary>
public sealed class WebTamperService : IWebTamperService, IDisposable
{
    private const string EventStateDomain = "web-tamper.event";
    public const int MaxContentBytes = 2 * 1024 * 1024;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _dataFile;
    private readonly string _contentDirectory;
    private readonly string _remoteCacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedFetch> _fetchCache = new(StringComparer.Ordinal);
    private readonly object _queueLock = new();
    private readonly Dictionary<string, DateTime> _lastQueuedChecks = new(StringComparer.Ordinal);
    private readonly Channel<string> _checkQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly string _bypassToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebTamperStore _store;

    private readonly PrometheusMetrics _metrics;
    private readonly IRuntimeStateStore _runtimeState;

    public WebTamperService(PrometheusMetrics metrics, IRuntimeStateStore runtimeState)
    {
        _metrics = metrics;
        _runtimeState = runtimeState;
        var directory = Path.Combine(AppContext.BaseDirectory, "datas", "web-tamper");
        Directory.CreateDirectory(directory);
        _dataFile = Path.Combine(directory, "resources.json");
        _contentDirectory = Path.Combine(directory, "content");
        Directory.CreateDirectory(_contentDirectory);
        _remoteCacheDirectory = Path.Combine(directory, "remote-cache");
        Directory.CreateDirectory(_remoteCacheDirectory);
        foreach (var staleFile in Directory.EnumerateFiles(_remoteCacheDirectory, "*.bin")) TryDelete(staleFile);
        _store = Load();
        foreach (var item in _store.Events) SyncEvent(item);
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LyWaf-TamperMonitor", "1.0"));
    }

    public IReadOnlyList<WebTamperResource> GetResources()
    {
        lock (_lock)
        {
            return _store.Resources
                .Where(item => item.ParentResourceId == null)
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item =>
                {
                    var clone = Clone(item);
                    clone.CapturedCount = _store.Resources.Count(child => child.ParentResourceId == item.Id);
                    clone.Changed = clone.Changed || _store.Resources.Any(child => child.ParentResourceId == item.Id && child.Enabled && child.Changed);
                    if (item.IsPattern)
                    {
                        clone.CapturedResources = _store.Resources
                            .Where(child => child.ParentResourceId == item.Id)
                            .OrderBy(child => child.Path, StringComparer.Ordinal)
                            .Select(Clone)
                            .ToList();
                    }
                    return clone;
                }).ToList();
        }
    }

    public IReadOnlyList<WebTamperEvent> GetEvents()
    {
        lock (_lock)
        {
            var events = _store.Events
                .GroupBy(EventKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Clone(group.OrderByDescending(item => item.DetectedAt).First()), StringComparer.Ordinal);
            foreach (var (key, distributed) in _runtimeState.GetValues<WebTamperEvent>(EventStateDomain))
                events[key] = Clone(distributed);
            return events.Values
                .OrderByDescending(item => item.DetectedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public IReadOnlyList<WebTamperWebsite> GetWebsites()
    {
        lock (_lock) return _store.Websites.OrderBy(item => item.Name).Select(Clone).ToList();
    }

    public WebTamperWebsite AddWebsite(string name, string origin, int? port = null)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 80) throw new ArgumentException("网站名称不能为空且不能超过 80 个字符");
        origin = NormalizeDomain(origin, port);
        lock (_lock)
        {
            if (_store.Websites.Any(item => string.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("该网站已经存在");
            var website = new WebTamperWebsite { Name = name, Origin = origin, Source = "manual" };
            _store.Websites.Add(website);
            Save();
            return Clone(website);
        }
    }

    public WebTamperWebsite RenameWebsite(string name, string origin, string source)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 80) throw new ArgumentException("备注名称不能为空且不能超过 80 个字符");
        origin = NormalizeDomain(origin);
        source = source == "config" ? "config" : "manual";
        lock (_lock)
        {
            var website = _store.Websites.FirstOrDefault(item => string.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase));
            if (website == null)
            {
                website = new WebTamperWebsite { Name = name, Origin = origin, Source = source };
                _store.Websites.Add(website);
            }
            else
            {
                website.Name = name;
                if (website.Source != "manual") website.Source = source;
            }
            Save();
            return Clone(website);
        }
    }

    public bool DeleteWebsite(string id)
    {
        lock (_lock)
        {
            var website = _store.Websites.FirstOrDefault(item => item.Id == id);
            if (website == null) return false;
            if (_store.Resources.Any(item => string.Equals(item.Website, website.Origin, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("该网站仍有保护资源，请先删除资源");
            _store.Websites.Remove(website);
            Save();
            return true;
        }
    }

    public async Task<WebTamperResource> AddAsync(string website, string path, CancellationToken cancellationToken)
    {
        website = NormalizeDomain(website);
        path = NormalizePath(path);
        var isPattern = IsPathPattern(path);
        lock (_lock)
        {
            if (_store.Resources.Any(item => string.Equals(item.Website, website, StringComparison.OrdinalIgnoreCase) && item.Path == path))
                throw new ArgumentException("该网站的资源路径已经受到保护");
        }

        var now = DateTime.UtcNow;
        var resource = new WebTamperResource
        {
            Name = path,
            Website = website,
            Path = path,
            Url = isPattern ? "" : website + path,
            IsPattern = isPattern,
            CreatedAt = now,
            UpdatedAt = now
        };
        CachedFetch? initialContent = null;
        if (!isPattern)
        {
            var fetched = await FetchWebsiteAsync(website, path, cancellationToken);
            initialContent = fetched;
            resource.Url = fetched.Url;
            resource.ContentType = fetched.ContentType;
            resource.BaselineHash = fetched.Hash;
            resource.CurrentHash = resource.BaselineHash;
            resource.LastCheckedAt = now;
        }
        lock (_lock)
        {
            if (initialContent != null) File.Copy(initialContent.FilePath, ContentPath(resource.Id), true);
            _store.Resources.Add(resource);
            Save();
        }
        return Clone(resource);
    }

    public bool Update(string id, string name, bool enabled)
    {
        lock (_lock)
        {
            var resource = _store.Resources.FirstOrDefault(item => item.Id == id);
            if (resource == null) return false;
            name = name.Trim();
            if (name.Length == 0 || name.Length > 80) throw new ArgumentException("资源名称不能为空且不能超过 80 个字符");
            resource.Name = name;
            resource.Enabled = enabled;
            resource.UpdatedAt = DateTime.UtcNow;
            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var target = _store.Resources.FirstOrDefault(item => item.Id == id);
            if (target == null) return false;
            var ids = _store.Resources.Where(item => item.Id == id || item.ParentResourceId == id).Select(item => item.Id).ToHashSet();
            _store.Resources.RemoveAll(item => ids.Contains(item.Id));
            _store.Events.RemoveAll(item => ids.Contains(item.ResourceId)
                || (target.ParentResourceId != null && item.ResourceId == target.ParentResourceId && item.ResourceName == target.Path));
            foreach (var resourceId in ids)
            {
                var path = ContentPath(resourceId);
                if (File.Exists(path)) File.Delete(path);
            }
            Save();
            return true;
        }
    }

    public bool IsInternalCheck(HttpRequest request) =>
        request.Headers.TryGetValue("X-LyWaf-Tamper-Check", out var value)
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(value.ToString()),
            System.Text.Encoding.ASCII.GetBytes(_bypassToken));

    public bool TryGetProtectedContent(HttpRequest request, out WebTamperResource? resource, out ProtectedContent? baseline)
    {
        resource = null;
        baseline = null;
        if (!HttpMethods.IsGet(request.Method)) return false;
        lock (_lock)
        {
            var requestPathOnly = $"{request.PathBase}{request.Path}";
            var requestPath = $"{requestPathOnly}{request.QueryString}";
            var match = _store.Resources
                .Where(item => item.ParentResourceId == null && item.Enabled
                    && string.Equals(item.Website, NormalizeRequestHost(request.Host), StringComparison.OrdinalIgnoreCase)
                    && ((!item.IsPattern && string.Equals(item.Path, requestPath, StringComparison.Ordinal))
                        || (item.IsPattern && PathMatches(item.Path, requestPathOnly))))
                .OrderBy(item => item.IsPattern ? 1 : 0)
                .ThenByDescending(item => LiteralPrefixLength(item.Path))
                .FirstOrDefault();
            if (match == null) return false;
            var concrete = match.IsPattern
                ? _store.Resources.FirstOrDefault(item => item.ParentResourceId == match.Id && item.Path == requestPath)
                : match;
            if (concrete is { Enabled: false }) return false;
            resource = Clone(concrete ?? match);
            if (concrete != null || !match.IsPattern)
            {
                var path = ContentPath(resource.Id);
                if (File.Exists(path)) baseline = new ProtectedContent(path, resource.ContentType, new FileInfo(path).Length);
            }
            return true;
        }
    }

    public void RecordObservedContent(WebTamperResource resource, string requestPath, string contentType, string contentFilePath, string currentHash)
    {
        lock (_lock)
        {
            var stored = _store.Resources.FirstOrDefault(item => item.Id == resource.Id);
            if (stored == null) return;
            if (stored.IsPattern && stored.ParentResourceId == null)
            {
                var existing = _store.Resources.FirstOrDefault(item => item.ParentResourceId == stored.Id && item.Path == requestPath);
                if (existing != null)
                {
                    stored = existing;
                }
                else
                {
                var child = new WebTamperResource
                {
                    Name = requestPath,
                    Website = stored.Website,
                    Path = requestPath,
                    Url = $"https://{stored.Website}{requestPath}",
                    ContentType = contentType,
                    BaselineHash = currentHash,
                    CurrentHash = currentHash,
                    ParentResourceId = stored.Id,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastCheckedAt = DateTime.UtcNow
                };
                File.Copy(contentFilePath, ContentPath(child.Id), true);
                _store.Resources.Add(child);
                stored.CapturedCount++;
                stored.UpdatedAt = DateTime.UtcNow;
                Save();
                Logger.Info("网页防篡改通配规则已建立资源基线: {Pattern} -> {Path}", stored.Path, requestPath);
                return;
                }
            }
            var now = DateTime.UtcNow;
            var previousChanged = stored.Changed;
            stored.CurrentHash = currentHash;
            var shouldPersistCheckTime = !stored.LastCheckedAt.HasValue || now - stored.LastCheckedAt.Value >= TimeSpan.FromMinutes(5);
            stored.LastCheckedAt = now;
            stored.Changed = !string.Equals(stored.BaselineHash, currentHash, StringComparison.Ordinal);
            var eventChanged = false;
            if (stored.Changed)
            {
                var eventResourceId = stored.ParentResourceId ?? stored.Id;
                var existingEvent = _store.Events
                    .Where(item => item.ResourceId == eventResourceId && item.ResourceName == stored.Path && !item.Resolved)
                    .OrderByDescending(item => item.DetectedAt)
                    .FirstOrDefault();
                if (existingEvent == null)
                {
                    existingEvent = new WebTamperEvent
                    {
                        ResourceId = eventResourceId,
                        ResourceName = stored.Path
                    };
                    _store.Events.Add(existingEvent);
                }
                existingEvent.Url = stored.Url;
                existingEvent.BaselineHash = stored.BaselineHash;
                existingEvent.CurrentHash = currentHash;
                existingEvent.DetectedAt = now;
                SyncEvent(existingEvent);
                eventChanged = true;
                _store.Events.RemoveAll(item => item.Id != existingEvent.Id && item.ResourceId == eventResourceId
                    && item.ResourceName == stored.Path && !item.Resolved);
                if (_store.Events.Count > 500) _store.Events.RemoveRange(0, _store.Events.Count - 500);
                Logger.Warn("网页内容变更，已返回安全基线: {Name} {Url}", stored.Name, stored.Url);
            }
            else
            {
                var eventResourceId = stored.ParentResourceId ?? stored.Id;
                var pendingEvents = _store.Events.Where(item => item.ResourceId == eventResourceId
                    && item.ResourceName == stored.Path && !item.Resolved).ToList();
                if (pendingEvents.Count > 0)
                {
                    foreach (var pendingEvent in pendingEvents)
                    {
                        pendingEvent.Resolved = true;
                        pendingEvent.ResolvedAt = now;
                        SyncEvent(pendingEvent);
                    }
                    eventChanged = true;
                    Logger.Info("网页内容已恢复至安全基线，自动确认变更事件: {Name} {Url}", stored.Name, stored.Url);
                }
            }
            if (eventChanged || previousChanged != stored.Changed || shouldPersistCheckTime) Save();
        }
    }

    public bool TryQueueBackgroundCheck(string website, string requestPath)
    {
        string? resourceId;
        lock (_lock)
        {
            resourceId = _store.Resources
                .Where(item => item.Enabled && string.Equals(item.Website, website, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(item.Path, requestPath, StringComparison.Ordinal)
                        || (item.ParentResourceId != null && string.Equals(item.Path, requestPath, StringComparison.Ordinal))))
                .OrderByDescending(item => item.ParentResourceId != null)
                .Select(item => item.Id)
                .FirstOrDefault();
        }
        if (resourceId == null) return false;

        var now = DateTime.UtcNow;
        lock (_queueLock)
        {
            if (_lastQueuedChecks.TryGetValue(resourceId, out var lastQueued)
                && now - lastQueued < TimeSpan.FromMinutes(1)) return false;
            if (!_checkQueue.Writer.TryWrite(resourceId))
            {
                _metrics.WebTamperQueueDropped();
                return false;
            }
            _metrics.WebTamperQueued();
            _lastQueuedChecks[resourceId] = now;
            if (_lastQueuedChecks.Count > 20_000)
            {
                var cutoff = now - TimeSpan.FromMinutes(2);
                foreach (var staleId in _lastQueuedChecks.Where(item => item.Value < cutoff).Select(item => item.Key).ToList())
                    _lastQueuedChecks.Remove(staleId);
            }
            return true;
        }
    }

    public async Task ProcessBackgroundChecksAsync(CancellationToken cancellationToken)
    {
        await foreach (var resourceId in _checkQueue.Reader.ReadAllAsync(cancellationToken))
        {
            _metrics.WebTamperDequeued();
            try { await CheckAsync(resourceId, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Logger.Warn(ex, "网页防篡改后台惰性检查失败: {ResourceId}", resourceId); }
        }
    }

    public async Task<ContentComparison?> CheckAsync(string id, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var comparison = await CheckCoreAsync(id, cancellationToken);
            outcome = comparison == null ? "not_found" : comparison.Changed ? "changed" : "unchanged";
            return comparison;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "timeout";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            _metrics.ObserveWebTamperCheck(outcome, Stopwatch.GetElapsedTime(startedAt));
        }
    }

    private async Task<ContentComparison?> CheckCoreAsync(string id, CancellationToken cancellationToken)
    {
        WebTamperResource? resource;
        lock (_lock) resource = _store.Resources.FirstOrDefault(item => item.Id == id) is { } item ? Clone(item) : null;
        if (resource == null) return null;
        if (resource.IsPattern && resource.ParentResourceId == null)
        {
            List<string> childIds;
            lock (_lock) childIds = _store.Resources.Where(item => item.ParentResourceId == id && item.Enabled).Select(item => item.Id).ToList();
            if (childIds.Count == 0) throw new InvalidOperationException("该规则尚未捕获任何实际资源，请先访问匹配的页面");
            ContentComparison? first = null;
            foreach (var childId in childIds)
            {
                var result = await CheckAsync(childId, cancellationToken);
                if (result?.Changed == true) return result;
                first ??= result;
            }
            return first;
        }
        var fetched = await FetchWebsiteAsync(resource.Website, resource.Path, cancellationToken);
        RecordObservedContent(resource, resource.Path, fetched.ContentType, fetched.FilePath, fetched.Hash);
        lock (_lock) resource = Clone(_store.Resources.First(item => item.Id == id));
        return new ContentComparison(resource, resource.Changed,
            await ToPreviewAsync(ContentPath(id), cancellationToken),
            await ToPreviewAsync(fetched.FilePath, cancellationToken));
    }

    public async Task<ContentComparison?> AcceptCurrentAsync(string id, CancellationToken cancellationToken)
    {
        WebTamperResource? resource;
        lock (_lock) resource = _store.Resources.FirstOrDefault(item => item.Id == id) is { } item ? Clone(item) : null;
        if (resource == null) return null;
        var fetched = await FetchWebsiteAsync(resource.Website, resource.Path, cancellationToken);
        var hash = fetched.Hash;
        lock (_lock)
        {
            var stored = _store.Resources.First(item => item.Id == id);
            File.Copy(fetched.FilePath, ContentPath(id), true);
            stored.BaselineHash = hash;
            stored.CurrentHash = hash;
            stored.Url = fetched.Url;
            stored.ContentType = fetched.ContentType;
            stored.Changed = false;
            stored.UpdatedAt = DateTime.UtcNow;
            stored.LastCheckedAt = DateTime.UtcNow;
            foreach (var evt in _store.Events.Where(item => !item.Resolved
                && (item.ResourceId == id
                    || (stored.ParentResourceId != null && item.ResourceId == stored.ParentResourceId && item.ResourceName == stored.Path))))
            {
                evt.Resolved = true;
                evt.ResolvedAt = DateTime.UtcNow;
                SyncEvent(evt);
            }
            Save();
            resource = Clone(stored);
        }
        var preview = await ToPreviewAsync(fetched.FilePath, cancellationToken);
        return new ContentComparison(resource, false, preview, preview);
    }

    public async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var resource in GetResources().Where(item => item.Enabled))
        {
            try { await CheckAsync(resource.Id, cancellationToken); }
            catch (Exception ex) { Logger.Warn(ex, "网页防篡改检查失败: {Url}", resource.Url); }
        }
    }

    private async Task<CachedFetch> FetchAsync(string url, string cacheKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-LyWaf-Tamper-Check", _bypassToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > MaxContentBytes) throw new ArgumentException("资源内容超过 2 MB 限制");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var destinationPath = Path.Combine(_remoteCacheDirectory, $"{CacheFileName(cacheKey)}-{Guid.NewGuid():N}.bin");
        var buffer = new byte[81920];
        long total = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxContentBytes) throw new ArgumentException("资源内容超过 2 MB 限制");
                hasher.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
        return new CachedFetch(destinationPath,
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
            Convert.ToHexString(hasher.GetHashAndReset()), total, url, DateTime.UtcNow);
    }

    private async Task<CachedFetch> FetchWebsiteAsync(string domain, string path, CancellationToken cancellationToken)
    {
        var cacheKey = $"{domain}\n{path}";
        if (TryGetCachedFetch(cacheKey, out var cached)) return cached;
        var gate = _fetchLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedFetch(cacheKey, out cached)) return cached;
            Exception? httpsError = null;
            foreach (var scheme in new[] { "https", "http" })
            {
                var url = $"{scheme}://{domain}{path}";
                try
                {
                    var fetched = await FetchAsync(url, cacheKey, cancellationToken);
                    _fetchCache.AddOrUpdate(cacheKey, fetched, (_, _) => fetched);
                    CleanupRemoteCache();
                    return fetched;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (scheme == "https") httpsError = ex;
                    else throw new HttpRequestException(
                        $"HTTPS 和 HTTP 均无法访问\nHTTPS：{httpsError?.Message}\nHTTP：{ex.Message}", ex);
                }
            }
            throw new HttpRequestException("网站无法访问");
        }
        finally { gate.Release(); }
    }

    private bool TryGetCachedFetch(string key, out CachedFetch cached)
    {
        if (_fetchCache.TryGetValue(key, out cached!)
            && DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(1)
            && File.Exists(cached.FilePath)) return true;
        cached = null!;
        return false;
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS URL");
        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimEnd('/');
    }

    private static string NormalizeDomain(string value, int? port = null)
    {
        value = value.Trim();
        if (port is < 1 or > 65535) throw new ArgumentException("端口必须在 1 到 65535 之间");
        var candidate = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) throw new ArgumentException("请输入有效的域名");
        var host = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || (Uri.CheckHostName(host) == UriHostNameType.Unknown && !System.Net.IPAddress.TryParse(host, out _)))
            throw new ArgumentException("请输入有效的域名");
        var effectivePort = port ?? (uri.IsDefaultPort ? null : uri.Port);
        return FormatAuthority(host, effectivePort);
    }

    private static string NormalizeRequestHost(HostString host) =>
        FormatAuthority(host.Host.TrimEnd('.').ToLowerInvariant(), host.Port);

    private static string FormatAuthority(string host, int? port)
    {
        var formattedHost = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return port.HasValue ? $"{formattedHost}:{port.Value}" : formattedHost;
    }

    private static string NormalizePath(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "/";
        if (!value.StartsWith('/')) value = "/" + value;
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('#'))
            throw new ArgumentException("请输入有效的资源路径");
        return value;
    }

    private static bool IsPathPattern(string path) => path.Contains('*') || path.Contains("{**", StringComparison.Ordinal);

    private static bool PathMatches(string pattern, string path)
    {
        var queryIndex = pattern.IndexOf('?');
        if (queryIndex >= 0) pattern = pattern[..queryIndex];
        var catchAll = pattern.IndexOf("{**", StringComparison.Ordinal);
        if (catchAll >= 0) return path.StartsWith(pattern[..catchAll], StringComparison.Ordinal);
        var wildcard = pattern.IndexOf('*');
        if (wildcard >= 0) return path.StartsWith(pattern[..wildcard], StringComparison.Ordinal);
        return string.Equals(pattern, path, StringComparison.Ordinal);
    }

    private static int LiteralPrefixLength(string pattern)
    {
        var wildcard = pattern.IndexOf('*');
        var catchAll = pattern.IndexOf('{');
        var index = wildcard < 0 ? catchAll : catchAll < 0 ? wildcard : Math.Min(wildcard, catchAll);
        return index < 0 ? pattern.Length : index;
    }

    private string ContentPath(string id) => Path.Combine(_contentDirectory, id + ".bin");
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<string> ToPreviewAsync(string path, CancellationToken cancellationToken)
    {
        const int maxPreviewBytes = 200_000;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = (int)Math.Min(stream.Length, maxPreviewBytes);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private static string CacheFileName(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private void CleanupRemoteCache()
    {
        var activeFiles = _fetchCache.Values.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var file in Directory.EnumerateFiles(_remoteCacheDirectory, "*.bin"))
        {
            if (!activeFiles.Contains(file) && File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
        }
    }

    private WebTamperStore Load()
    {
        try
        {
            WebTamperStore store;
            if (File.Exists(_dataFile))
            {
                using var stream = new FileStream(_dataFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.SequentialScan);
                store = JsonSerializer.Deserialize<WebTamperStore>(stream, JsonOptions) ?? new();
            }
            else store = new();
            foreach (var resource in store.Resources)
            {
                if (resource.Website.Length > 0) resource.Website = NormalizeDomain(resource.Website);
                else if (Uri.TryCreate(resource.Url, UriKind.Absolute, out var uri)) resource.Website = uri.Host.ToLowerInvariant();
                if (Uri.TryCreate(resource.Url, UriKind.Absolute, out var resourceUri) && (resource.Path.Length == 0 || resource.Path == "/"))
                    resource.Path = string.IsNullOrEmpty(resourceUri.PathAndQuery) ? "/" : resourceUri.PathAndQuery;
                if (resource.Name.Length == 0) resource.Name = resource.Path;
            }
            foreach (var website in store.Websites) website.Origin = NormalizeDomain(website.Origin);
            return store;
        }
        catch (Exception ex) { Logger.Error(ex, "加载网页防篡改数据失败"); return new(); }
    }

    private void Save()
    {
        var temp = _dataFile + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.SequentialScan))
        {
            JsonSerializer.Serialize(stream, _store, JsonOptions);
            stream.Flush(true);
        }
        File.Move(temp, _dataFile, true);
    }

    private static WebTamperResource Clone(WebTamperResource item) => new()
    {
        Id = item.Id, Name = item.Name, Website = item.Website, Path = item.Path, Url = item.Url,
        Enabled = item.Enabled, ContentType = item.ContentType,
        BaselineHash = item.BaselineHash, CurrentHash = item.CurrentHash, CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt, LastCheckedAt = item.LastCheckedAt, Changed = item.Changed,
        IsPattern = item.IsPattern, ParentResourceId = item.ParentResourceId, CapturedCount = item.CapturedCount
    };
    private static WebTamperEvent Clone(WebTamperEvent item) => new()
    {
        Id = item.Id, ResourceId = item.ResourceId, ResourceName = item.ResourceName, Url = item.Url,
        BaselineHash = item.BaselineHash, CurrentHash = item.CurrentHash, DetectedAt = item.DetectedAt,
        Resolved = item.Resolved, ResolvedAt = item.ResolvedAt
    };

    private void SyncEvent(WebTamperEvent item)
        => _runtimeState.Set(EventStateDomain, EventKey(item), Clone(item), TimeSpan.FromDays(30));

    private static string EventKey(WebTamperEvent item) => $"{item.ResourceId}:{item.ResourceName}";
    private static WebTamperWebsite Clone(WebTamperWebsite item) => new()
    {
        Id = item.Id, Name = item.Name, Origin = item.Origin, Source = item.Source, CreatedAt = item.CreatedAt
    };

    public void Dispose()
    {
        _httpClient.Dispose();
        foreach (var item in _fetchCache.Values) TryDelete(item.FilePath);
        foreach (var gate in _fetchLocks.Values) gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record CachedFetch(string FilePath, string ContentType, string Hash, long Length, string Url, DateTime FetchedAt);

    private sealed class WebTamperStore
    {
        public List<WebTamperWebsite> Websites { get; set; } = [];
        public List<WebTamperResource> Resources { get; set; } = [];
        public List<WebTamperEvent> Events { get; set; } = [];
    }
}

/// <summary>消费防篡改惰性检查队列；仅在插件运行时执行后台远程检查。</summary>
public sealed class WebTamperMonitorService(IWebTamperService service, LyWaf.Plugins.Core.IPluginManager pluginManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueTask = service.ProcessBackgroundChecksAsync(stoppingToken);
        var dailyTask = RunDailyChecksAsync(stoppingToken);
        await Task.WhenAll(queueTask, dailyTask);
    }

    private async Task RunDailyChecksAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromDays(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (pluginManager.GetPluginState("web-tamper") is not (LyWaf.Plugins.Core.PluginState.Running or LyWaf.Plugins.Core.PluginState.Initialized))
                continue;
            await service.CheckAllAsync(stoppingToken);
        }
    }
}
