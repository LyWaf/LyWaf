namespace LyWaf.Services.Files;

public class FileMetadata
{
    public static readonly string UnspportContentType = "waf/not-support";
    public string ContentType { get; set; } = "application/octet-stream";
    public DateTime LastModified { get; set; }
    public long Size { get; set; }
    public string ETag { get; set; } = string.Empty;

    public bool IsNotSupport() {
        return ContentType == UnspportContentType;
    }
}