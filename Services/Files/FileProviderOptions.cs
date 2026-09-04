namespace LyWaf.Services.Files;

/// <summary>所有静态文件路由共享的 MIME 与安全默认值。</summary>
public sealed class FileProviderOptions
{
    /// <summary>文件元数据缓存周期；保留用于配置兼容。</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>旧版允许扩展名字段；为配置兼容保留，当前采用 MIME 默认值与拒绝列表控制。</summary>
    public HashSet<string> AllowedExtensions { get; set; } = [".txt", ".pdf", ".jpg", ".png", ".gif", ".mp4", ".mp3", ".zip"];
    /// <summary>自定义扩展名到 MIME 的映射，键应包含句点。</summary>
    public Dictionary<string, string> MimeExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase) { [".txt"] = "text/plain" };
    /// <summary>全局禁止下载的扩展名。</summary>
    public HashSet<string> RemoveExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>是否处理 Range；关闭时忽略 Range 并返回完整文件。</summary>
    public bool EnableRangeProcessing { get; set; } = true;
    /// <summary>旧版元数据缓存开关；为配置兼容保留，安全检查始终实时执行。</summary>
    public bool EnableCaching { get; set; } = true;
}

/// <summary>传给文件解析服务的单路由配置快照。</summary>
public sealed class FileEveryConfig
{
    /// <summary>目录请求依次尝试的索引文件。</summary>
    public HashSet<string> Default { get; set; } = ["index.html", "index.htm"];
    /// <summary>允许发送的单文件最大字节数。</summary>
    public long MaxFileSize { get; set; } = 100 * 1024 * 1024;
    /// <summary>静态文件根目录。</summary>
    public string BasePath { get; set; } = "wwwroot/files";
    /// <summary>按顺序尝试的候选路径模板。</summary>
    public string[]? TryFiles { get; set; }
    /// <summary>是否允许目录浏览。</summary>
    public bool Browse { get; set; }
    /// <summary>单次目录浏览最多列出的项目数。</summary>
    public int MaxBrowseEntries { get; set; } = 1000;
    /// <summary>是否按 Accept-Encoding 选择同名预压缩文件。</summary>
    public bool PreCompressed { get; set; }
    /// <summary>是否允许访问链接或重解析点。</summary>
    public bool FollowLinks { get; set; }
    /// <summary>是否允许访问隐藏项。</summary>
    public bool ShowHidden { get; set; }
    /// <summary>静态文件响应的 Cache-Control。</summary>
    public string CacheControl { get; set; } = "public, max-age=3600";
    /// <summary>路由禁止公开的扩展名集合。</summary>
    public HashSet<string> DeniedExtensions { get; set; } = FileServerItem.CreateDefaultDeniedExtensions();
}

/// <summary>磁盘路径解析结果，包含是否为目录及所选预压缩编码。</summary>
public sealed class FileGetInfo(string path, bool isDirectory = false, string? compress = null)
{
    /// <summary>规范化物理路径。</summary>
    public string Path { get; set; } = path;
    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; set; } = isDirectory;
    /// <summary>所选预压缩编码。</summary>
    public string? PreCompressed { get; set; } = compress;
}
