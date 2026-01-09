namespace LyWaf.Services.Files;

public class FileServiceResult
{
    public Stream? Stream { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public long FileLength { get; set; }
    public long TotalLength { get; set; }
    public DateTime LastModified { get; set; }
    public string ETag { get; set; } = string.Empty;
    public int StatusCode { get; set; } = StatusCodes.Status200OK;
    public long? RangeStart { get; set; }
    public long? RangeEnd { get; set; }
    public string? ErrMsg { get; set; }

    public static FileServiceResult NotFound() => new() { StatusCode = StatusCodes.Status404NotFound };
    public static FileServiceResult NoSupportContentType() => new() { StatusCode = StatusCodes.Status403Forbidden, ErrMsg = "The extension is not support" };
    public static FileServiceResult FileToBigger() => new() { StatusCode = StatusCodes.Status403Forbidden, ErrMsg = "File Too Bigger" };
}