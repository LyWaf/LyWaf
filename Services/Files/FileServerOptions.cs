namespace LyWaf.Services.Files;

/// <summary>
/// 文件服务器配置选项，按路由 ID 映射
/// 路由 ID 格式为 fileserver_xxx
/// </summary>
public class FileServerOptions
{
    /// <summary>
    /// 文件服务配置项，键为路由 ID（fileserver_xxx）
    /// </summary>
    public Dictionary<string, FileServerItem> Items { get; set; } = [];
}

/// <summary>
/// 单个文件服务配置项
/// </summary>
public class FileServerItem
{
    /// <summary>
    /// 基础路径（文件服务的根目录）
    /// </summary>
    public string BasePath { get; set; } = "wwwroot";

    /// <summary>
    /// 默认索引文件
    /// </summary>
    public HashSet<string> Default { get; set; } = ["index.html", "index.htm"];

    /// <summary>
    /// 最大文件大小（字节），默认 100MB
    /// </summary>
    public long MaxFileSize { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// try_files 配置
    /// </summary>
    public string[]? TryFiles { get; set; } = null;

    /// <summary>
    /// 是否启用目录浏览
    /// </summary>
    public bool Browse { get; set; } = false;

    /// <summary>
    /// 是否启用预压缩文件（.gz, .br 等）
    /// </summary>
    public bool PreCompressed { get; set; } = false;

    /// <summary>
    /// URL 前缀（用于映射请求路径）
    /// </summary>
    public string Prefix { get; set; } = "/";

    /// <summary>
    /// 转换为 FileEveryConfig（兼容现有的 FileService）
    /// </summary>
    public FileEveryConfig ToFileEveryConfig()
    {
        return new FileEveryConfig
        {
            BasePath = BasePath,
            Default = Default,
            MaxFileSize = MaxFileSize,
            TryFiles = TryFiles,
            Browse = Browse,
            PreCompressed = PreCompressed
        };
    }
}
