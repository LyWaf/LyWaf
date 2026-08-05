using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace LyWaf.Services.WebTamper;

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

public sealed class WebTamperWebsite
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

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

public sealed record ProtectedContent(byte[] Bytes, string ContentType);
public sealed record ContentComparison(WebTamperResource Resource, bool Changed, string BaselineContent, string CurrentContent);

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
    void RecordObservedContent(WebTamperResource resource, string requestPath, string contentType, ReadOnlySpan<byte> content);
    Task<ContentComparison?> CheckAsync(string id, CancellationToken cancellationToken);
    Task<ContentComparison?> AcceptCurrentAsync(string id, CancellationToken cancellationToken);
    Task CheckAllAsync(CancellationToken cancellationToken);
}

public sealed class WebTamperService : IWebTamperService, IDisposable
{
    public const int MaxContentBytes = 2 * 1024 * 1024;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _dataFile;
    private readonly string _contentDirectory;
    private readonly HttpClient _httpClient;
    private readonly string _bypassToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebTamperStore _store;

    public WebTamperService()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "datas", "web-tamper");
        Directory.CreateDirectory(directory);
        _dataFile = Path.Combine(directory, "resources.json");
        _contentDirectory = Path.Combine(directory, "content");
        Directory.CreateDirectory(_contentDirectory);
        _store = Load();
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
            return _store.Events
                .GroupBy(item => $"{item.ResourceId}\n{item.ResourceName}", StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.DetectedAt).First())
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
        ProtectedContent? initialContent = null;
        if (!isPattern)
        {
            var fetched = await FetchWebsiteAsync(website, path, cancellationToken);
            initialContent = fetched.Content;
            resource.Url = fetched.Url;
            resource.ContentType = fetched.Content.ContentType;
            resource.BaselineHash = Hash(fetched.Content.Bytes);
            resource.CurrentHash = resource.BaselineHash;
            resource.LastCheckedAt = now;
        }
        lock (_lock)
        {
            if (initialContent != null) File.WriteAllBytes(ContentPath(resource.Id), initialContent.Bytes);
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
                if (File.Exists(path)) baseline = new ProtectedContent(File.ReadAllBytes(path), resource.ContentType);
            }
            return true;
        }
    }

    public void RecordObservedContent(WebTamperResource resource, string requestPath, string contentType, ReadOnlySpan<byte> content)
    {
        var currentHash = Hash(content);
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
                File.WriteAllBytes(ContentPath(child.Id), content.ToArray());
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
                    }
                    eventChanged = true;
                    Logger.Info("网页内容已恢复至安全基线，自动确认变更事件: {Name} {Url}", stored.Name, stored.Url);
                }
            }
            if (eventChanged || previousChanged != stored.Changed || shouldPersistCheckTime) Save();
        }
    }

    public async Task<ContentComparison?> CheckAsync(string id, CancellationToken cancellationToken)
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
        RecordObservedContent(resource, resource.Path, fetched.Content.ContentType, fetched.Content.Bytes);
        var baseline = await File.ReadAllBytesAsync(ContentPath(id), cancellationToken);
        lock (_lock) resource = Clone(_store.Resources.First(item => item.Id == id));
        return new ContentComparison(resource, resource.Changed, ToPreview(baseline), ToPreview(fetched.Content.Bytes));
    }

    public async Task<ContentComparison?> AcceptCurrentAsync(string id, CancellationToken cancellationToken)
    {
        WebTamperResource? resource;
        lock (_lock) resource = _store.Resources.FirstOrDefault(item => item.Id == id) is { } item ? Clone(item) : null;
        if (resource == null) return null;
        var fetched = await FetchWebsiteAsync(resource.Website, resource.Path, cancellationToken);
        var hash = Hash(fetched.Content.Bytes);
        lock (_lock)
        {
            var stored = _store.Resources.First(item => item.Id == id);
            File.WriteAllBytes(ContentPath(id), fetched.Content.Bytes);
            stored.BaselineHash = hash;
            stored.CurrentHash = hash;
            stored.Url = fetched.Url;
            stored.ContentType = fetched.Content.ContentType;
            stored.Changed = false;
            stored.UpdatedAt = DateTime.UtcNow;
            stored.LastCheckedAt = DateTime.UtcNow;
            foreach (var evt in _store.Events.Where(item => !item.Resolved
                && (item.ResourceId == id
                    || (stored.ParentResourceId != null && item.ResourceId == stored.ParentResourceId && item.ResourceName == stored.Path))))
            {
                evt.Resolved = true;
                evt.ResolvedAt = DateTime.UtcNow;
            }
            Save();
            resource = Clone(stored);
        }
        return new ContentComparison(resource, false, ToPreview(fetched.Content.Bytes), ToPreview(fetched.Content.Bytes));
    }

    public async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var resource in GetResources().Where(item => item.Enabled))
        {
            try { await CheckAsync(resource.Id, cancellationToken); }
            catch (Exception ex) { Logger.Warn(ex, "网页防篡改检查失败: {Url}", resource.Url); }
        }
    }

    private async Task<ProtectedContent> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-LyWaf-Tamper-Check", _bypassToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > MaxContentBytes) throw new ArgumentException("资源内容超过 2 MB 限制");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + read > MaxContentBytes) throw new ArgumentException("资源内容超过 2 MB 限制");
            destination.Write(buffer, 0, read);
        }
        return new ProtectedContent(destination.ToArray(), response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }

    private async Task<(ProtectedContent Content, string Url)> FetchWebsiteAsync(string domain, string path, CancellationToken cancellationToken)
    {
        Exception? httpsError = null;
        foreach (var scheme in new[] { "https", "http" })
        {
            var url = $"{scheme}://{domain}{path}";
            try
            {
                return (await FetchAsync(url, cancellationToken), url);
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
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string ToPreview(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 200_000)));

    private WebTamperStore Load()
    {
        try
        {
            var store = File.Exists(_dataFile) ? JsonSerializer.Deserialize<WebTamperStore>(File.ReadAllText(_dataFile), JsonOptions) ?? new() : new();
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
        File.WriteAllText(temp, JsonSerializer.Serialize(_store, JsonOptions));
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
    private static WebTamperWebsite Clone(WebTamperWebsite item) => new()
    {
        Id = item.Id, Name = item.Name, Origin = item.Origin, Source = item.Source, CreatedAt = item.CreatedAt
    };

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class WebTamperStore
    {
        public List<WebTamperWebsite> Websites { get; set; } = [];
        public List<WebTamperResource> Resources { get; set; } = [];
        public List<WebTamperEvent> Events { get; set; } = [];
    }
}

public sealed class WebTamperMonitorService(IWebTamperService service) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromDays(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
            await service.CheckAllAsync(stoppingToken);
        }
    }
}
