namespace LyWaf.Services.Files;

/// <summary>用于条件请求和响应头生成的实时文件元数据。</summary>
public sealed class FileMetadata
{
    /// <summary>表示扩展名被策略拒绝的内部 MIME 标记。</summary>
    public static readonly string UnspportContentType = "waf/not-support";
    /// <summary>解析后的响应 MIME 类型。</summary>
    public string ContentType { get; set; } = "application/octet-stream";
    /// <summary>文件最后修改时间（UTC）。</summary>
    public DateTime LastModified { get; set; }
    /// <summary>文件字节长度。</summary>
    public long Size { get; set; }
    /// <summary>由修改时间和长度生成的带引号实体标签。</summary>
    public string ETag { get; set; } = string.Empty;

    /// <summary>扩展名是否被全局策略拒绝。</summary>
    public bool IsNotSupport() {
        return ContentType == UnspportContentType;
    }
}
