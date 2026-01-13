namespace LyWaf.Services.Compress;

/// <summary>
/// 响应压缩配置选项
/// </summary>
public class CompressOptions
{
    /// <summary>
    /// 是否启用响应压缩
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 是否启用 Brotli 压缩（优先于 gzip）
    /// </summary>
    public bool EnableBrotli { get; set; } = true;

    /// <summary>
    /// 是否启用 Gzip 压缩
    /// </summary>
    public bool EnableGzip { get; set; } = true;

    /// <summary>
    /// 压缩级别: Fastest, Optimal, NoCompression, SmallestSize
    /// </summary>
    public string Level { get; set; } = "Fastest";

    /// <summary>
    /// 最小响应大小（字节），小于此值不压缩
    /// 默认 10240 (10KB)
    /// </summary>
    public int MinSize { get; set; } = 10240;

    /// <summary>
    /// 需要压缩的 MIME 类型列表
    /// </summary>
    public List<string> MimeTypes { get; set; } = 
    [
        // 文本类型
        "text/plain",
        "text/html",
        "text/css",
        "text/xml",
        "text/javascript",
        
        // 应用类型
        "application/json",
        "application/javascript",
        "application/xml",
        "application/xhtml+xml",
        "application/rss+xml",
        "application/atom+xml",
        
        // 字体类型
        "font/woff",
        "font/woff2",
        "application/font-woff",
        "application/font-woff2",
        
        // 图像类型（SVG）
        "image/svg+xml",
        
        // 其他
        "application/x-javascript",
        "application/x-font-ttf",
        "application/vnd.ms-fontobject"
    ];

    /// <summary>
    /// 是否启用 HTTPS 压缩
    /// 注意：HTTPS 压缩可能存在 BREACH 攻击风险
    /// </summary>
    public bool EnableForHttps { get; set; } = true;
}
