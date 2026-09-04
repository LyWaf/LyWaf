namespace LyWaf.Services.Files;

/// <summary>描述一个已经过安全校验、可直接发送的静态文件响应。</summary>
public sealed class FileServiceResult
{
    /// <summary>规范化后的物理文件绝对路径。</summary>
    public string? PhysicalPath { get; init; }
    /// <summary>响应 MIME 类型。</summary>
    public string ContentType { get; init; } = "application/octet-stream";
    /// <summary>响应正文长度；多段 Range 未预计算时为 null。</summary>
    public long? ContentLength { get; init; }
    /// <summary>源文件完整长度。</summary>
    public long TotalLength { get; init; }
    /// <summary>源文件最后修改时间。</summary>
    public DateTime LastModified { get; init; }
    /// <summary>带引号的 HTTP 实体标签。</summary>
    public string ETag { get; init; } = string.Empty;
    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; init; } = StatusCodes.Status200OK;
    /// <summary>规范化后的一个或多个字节区间。</summary>
    public IReadOnlyList<FileByteRange> Ranges { get; init; } = [];
    /// <summary>多段 Range 响应使用的 MIME boundary。</summary>
    public string? Boundary { get; init; }
    /// <summary>可安全返回给客户端的简短错误信息。</summary>
    public string? ErrMsg { get; init; }

    public static FileServiceResult NotFound() => new() { StatusCode = StatusCodes.Status404NotFound };
    public static FileServiceResult NoSupportContentType() => new() { StatusCode = StatusCodes.Status403Forbidden, ErrMsg = "File type is not allowed" };
    public static FileServiceResult FileTooLarge() => new() { StatusCode = StatusCodes.Status403Forbidden, ErrMsg = "File is too large" };
    public static FileServiceResult RangeNotSatisfiable(long length) => new()
    {
        StatusCode = StatusCodes.Status416RangeNotSatisfiable,
        TotalLength = length
    };
}

/// <summary>静态文件中闭区间形式的字节范围。</summary>
public sealed record FileByteRange(long Start, long End)
{
    /// <summary>该区间包含的字节数。</summary>
    public long Length => End - Start + 1;
}
