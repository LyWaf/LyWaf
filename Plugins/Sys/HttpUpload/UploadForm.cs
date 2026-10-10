using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace LyWaf.Plugins.Sys.HttpUpload;

/// <summary>Reads small authentication fields first, leaving the single file as a bounded stream.</summary>
public sealed record UploadForm(Dictionary<string, string> Fields, MultipartReader Reader, Stream File)
{
    public static async Task<UploadForm> ReadAsync(HttpRequest request, long maxFileBytes, CancellationToken token)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !string.Equals(type.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected multipart/form-data.");
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new InvalidDataException("Invalid boundary.");
        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersCountLimit = 8, HeadersLengthLimit = 4096, BodyLengthLimit = maxFileBytes,
        };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadNextSectionAsync(token) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid form section.");
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
            if (name == "file")
            {
                var submittedName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue
                    ? disposition.FileNameStar : disposition.FileName).Value;
                if (!fields.TryGetValue("filename", out var filename) || submittedName != filename
                    || !new[] { "size", "timestamp", "nonce", "signature" }.All(fields.ContainsKey))
                    throw new InvalidDataException("Authentication fields must precede the matching file.");
                return new UploadForm(fields, reader, section.Body);
            }
            if (name is not ("filename" or "size" or "timestamp" or "nonce" or "signature" or "extract" or "directory")
                || fields.ContainsKey(name) || disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                throw new InvalidDataException("Unexpected or duplicate field.");
            using var data = new MemoryStream();
            await HttpUploadMiddleware.CopyBoundedAsync(section.Body, data, 1024, token);
            fields.Add(name, new System.Text.UTF8Encoding(false, true).GetString(data.ToArray()));
        }
        throw new InvalidDataException("Missing file.");
    }
}
