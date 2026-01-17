namespace LyWaf.Services.Files;

/// <summary>
/// 文件服务全局选项（缓存时间、MIME 类型映射等）
/// </summary>
public class FileProviderOptions
{
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);
    public HashSet<string> AllowedExtensions { get; set; } = [".txt", ".pdf", ".jpg", ".png", ".gif", ".mp4", ".mp3", ".zip"];
    public Dictionary<string, string> MimeExtensions { get; set; } = new Dictionary<string, string> {
        {".txt", "text/plain"},
    };

    public HashSet<string> RemoveExtensions { get; set; } = [];
    public bool EnableRangeProcessing { get; set; } = true;
    public bool EnableCaching { get; set; } = true;
}

public class FileEveryConfig
{
    public HashSet<string> Default { get; set; } = ["index.html", "index.htm"];
    public long MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB
    public string BasePath { get; set; } = "wwwroot/files";
    public string[]? TryFiles { get; set; } = null;
    // 是否显示结构
    public bool Browse { get; set; } = false;
    public bool PreCompressed { get; set; } = false;
}

public class FileGetInfo(string path, bool isDirectory = false, string? compress = null)
{
    public string Path { get; set; } = path;
    public bool IsDirectory { get; set; } = isDirectory;
    public string? PreCompressed { get; set; } = compress;
}