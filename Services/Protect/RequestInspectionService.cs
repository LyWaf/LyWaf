using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Protect;

/// <summary>Provides one bounded, normalized view of request values for all WAF detectors.</summary>
public interface IRequestInspectionService
{
    Task<RequestInspectionSnapshot> InspectAsync(HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>Contains normalized candidates extracted from one request without retaining an unbounded body.</summary>
public sealed record RequestInspectionSnapshot(
    IReadOnlyList<string> QueryValues,
    IReadOnlyList<string> HeaderValues,
    IReadOnlyList<string> BodyValues,
    bool BodyTruncated,
    IReadOnlyList<string> ProtocolAnomalies,
    IReadOnlyList<string>? NormalizationSteps = null);

public sealed class RequestInspectionService(IOptionsMonitor<ProtectOptions> options) : IRequestInspectionService
{
    private static readonly object SnapshotKey = new();
    public static RequestInspectionSnapshot? GetCompletedSnapshot(HttpContext context) =>
        context.Items.TryGetValue(SnapshotKey, out var cached) && cached is Task<RequestInspectionSnapshot> { IsCompletedSuccessfully: true } task ? task.Result : null;

    public Task<RequestInspectionSnapshot> InspectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (context.Items.TryGetValue(SnapshotKey, out var cached) &&
            cached is Task<RequestInspectionSnapshot> snapshotTask)
            return snapshotTask;

        var created = BuildSnapshotAsync(context, cancellationToken);
        context.Items[SnapshotKey] = created;
        return created;
    }

    private async Task<RequestInspectionSnapshot> BuildSnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var query = new CandidateCollector(settings);
        foreach (var item in context.Request.Query)
        {
            query.Add(item.Key);
            foreach (var value in item.Value) query.Add(value);
        }

        var headers = new CandidateCollector(settings);
        foreach (var item in context.Request.Headers)
        {
            headers.Add(item.Key);
            foreach (var value in item.Value) headers.Add(value);
        }
        var anomalies = FindProtocolAnomalies(context.Request);

        if (!MayHaveBody(context.Request))
            return new(query.Values, headers.Values, [], false, anomalies, query.Steps.Concat(headers.Steps).Distinct().ToArray());

        var (bodyBytes, truncated) = await ReadBodyAsync(
            context.Request,
            Math.Clamp(settings.InspectionMaxBodyBytes, 1024, 16 * 1024 * 1024),
            cancellationToken);
        var body = new CandidateCollector(settings);
        if (bodyBytes.Length > 0)
        {
            var decoded = DecodeContentEncoding(context.Request, bodyBytes,
                Math.Clamp(settings.InspectionMaxBodyBytes, 1024, 16 * 1024 * 1024));
            truncated |= decoded.Truncated;
            if (decoded.Error is not null) anomalies.Add(decoded.Error);
            await ExtractBodyValuesAsync(context.Request.ContentType, decoded.Bytes, body, cancellationToken);
        }
        return new(query.Values, headers.Values, body.Values, truncated, anomalies, query.Steps.Concat(headers.Steps).Concat(body.Steps).Distinct().ToArray());
    }

    private static bool MayHaveBody(HttpRequest request) =>
        request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding");

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        if (request.Body.CanSeek) request.Body.Position = 0;
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var truncated = false;
        try
        {
            while (output.Length <= maxBytes)
            {
                var remaining = Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length);
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            truncated = output.Length > maxBytes;
            return (output.GetBuffer().AsSpan(0, Math.Min((int)output.Length, maxBytes)).ToArray(), truncated);
        }
        finally
        {
            if (request.Body.CanSeek) request.Body.Position = 0;
        }
    }

    private static async Task ExtractBodyValuesAsync(
        string? contentType,
        byte[] bytes,
        CandidateCollector values,
        CancellationToken cancellationToken)
    {
        string content;
        try { content = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            values.Add(Convert.ToHexString(bytes));
            return;
        }
        if (contentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = 32,
                    AllowTrailingCommas = true
                });
                AddJson(document.RootElement, values);
                return;
            }
            catch (JsonException)
            {
                // Malformed JSON remains inspectable as raw text.
            }
        }
        else if (contentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var item in QueryHelpers.ParseQuery(content))
            {
                values.Add(item.Key);
                foreach (var value in item.Value) values.Add(value);
            }
            return;
        }
        else if (contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true)
        {
            ExtractXml(content, values);
            return;
        }
        else if (contentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            await ExtractMultipartAsync(contentType, bytes, values, cancellationToken);
            return;
        }

        values.Add(content);
    }

    private static void ExtractXml(string content, CandidateCollector values)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = content.Length,
                MaxCharactersFromEntities = 0
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    values.Add(reader.Name);
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            values.Add(reader.Name);
                            values.Add(reader.Value);
                        }
                        reader.MoveToElement();
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    values.Add(reader.Value);
            }
        }
        catch (XmlException) { values.Add(content); }
    }

    private static async Task ExtractMultipartAsync(
        string contentType,
        byte[] bytes,
        CandidateCollector values,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            values.Add(Encoding.UTF8.GetString(bytes));
            return;
        }
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 256)
        {
            values.Add(Encoding.UTF8.GetString(bytes));
            return;
        }
        try
        {
            var reader = new MultipartReader(boundary, new MemoryStream(bytes, writable: false));
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    values.Add(HeaderUtilities.RemoveQuotes(disposition.Name).Value);
                    values.Add(HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value);
                    values.Add(HeaderUtilities.RemoveQuotes(disposition.FileName).Value);
                }
                using var sectionBody = new MemoryStream();
                await section.Body.CopyToAsync(sectionBody, cancellationToken);
                values.Add(Encoding.UTF8.GetString(sectionBody.ToArray()));
            }
        }
        catch (IOException) { values.Add(Encoding.UTF8.GetString(bytes)); }
        catch (InvalidDataException) { values.Add(Encoding.UTF8.GetString(bytes)); }
    }

    private static (byte[] Bytes, bool Truncated, string? Error) DecodeContentEncoding(
        HttpRequest request,
        byte[] source,
        int maxBytes)
    {
        var encodings = request.Headers.ContentEncoding
            .SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            .Reverse()
            .ToArray();
        var current = source;
        foreach (var encoding in encodings)
        {
            if (encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var input = new MemoryStream(current, writable: false);
                using Stream decoder = encoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    _ => throw new NotSupportedException()
                };
                var result = ReadDecodedBounded(decoder, maxBytes);
                if (result.Truncated) return (result.Bytes, true, "解压后的请求体超过 WAF 检查上限");
                current = result.Bytes;
            }
            catch (NotSupportedException) { return (current, false, $"不支持的 Content-Encoding: {encoding}"); }
            catch (InvalidDataException) { return (current, false, $"无效的 {encoding} 压缩请求体"); }
        }
        return (current, false, null);
    }

    private static (byte[] Bytes, bool Truncated) ReadDecodedBounded(Stream source, int maxBytes)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (output.Length <= maxBytes)
        {
            var count = source.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length));
            if (count == 0) break;
            output.Write(buffer, 0, count);
        }
        var truncated = output.Length > maxBytes;
        return (output.GetBuffer().AsSpan(0, Math.Min((int)output.Length, maxBytes)).ToArray(), truncated);
    }

    private static List<string> FindProtocolAnomalies(HttpRequest request)
    {
        var result = new List<string>();
        var hasTransferEncoding = request.Headers.ContainsKey(HeaderNames.TransferEncoding);
        var hasContentLength = request.Headers.ContainsKey(HeaderNames.ContentLength) || request.ContentLength.HasValue;
        if (hasTransferEncoding && hasContentLength)
            result.Add("请求同时包含 Transfer-Encoding 和 Content-Length");
        if (request.Headers.TryGetValue(HeaderNames.ContentLength, out var lengths) &&
            lengths.SelectMany(static value => value?.Split(',') ?? []).Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal).Skip(1).Any())
            result.Add("请求包含冲突的 Content-Length");
        if (request.Headers.Any(header => header.Value.Any(value => value?.IndexOfAny(['\r', '\n', '\0']) >= 0)))
            result.Add("请求头包含非法控制字符");
        return result;
    }

    private static void AddJson(JsonElement element, CandidateCollector values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    values.Add(property.Name);
                    AddJson(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) AddJson(item, values);
                break;
            case JsonValueKind.String:
                values.Add(element.GetString());
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                values.Add(element.GetRawText());
                break;
        }
    }

    private sealed class CandidateCollector(ProtectOptions options)
    {
        public HashSet<string> Steps { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly int _maxCandidates = Math.Clamp(options.InspectionMaxCandidates, 8, 512);
        private readonly int _maxLength = Math.Clamp(options.InspectionMaxValueLength, 256, 1024 * 1024);
        private readonly int _maxDepth = Math.Clamp(options.InspectionDecodeDepth, 0, 5);

        public IReadOnlyList<string> Values => _seen.ToArray();

        public void Add(string? value)
        {
            if (string.IsNullOrEmpty(value) || _seen.Count >= _maxCandidates) return;
            AddRecursive(value.Length > _maxLength ? value[.._maxLength] : value, 0);
        }

        private void AddRecursive(string value, int depth)
        {
            if (!_seen.Add(value) || _seen.Count >= _maxCandidates || depth >= _maxDepth) return;
            var unicode = value.Normalize(NormalizationForm.FormKC);
            if (!string.Equals(unicode, value, StringComparison.Ordinal)) { Steps.Add("Unicode NFKC"); AddRecursive(unicode, depth + 1); }
            TryAdd(Uri.UnescapeDataString, value, depth);
            TryAdd(WebUtility.HtmlDecode, value, depth);
            if (TryDecodeBase64(value, out var base64)) { Steps.Add("Base64"); AddRecursive(base64, depth + 1); }
            if (TryDecodeHex(value, out var hex)) { Steps.Add("Hex"); AddRecursive(hex, depth + 1); }
        }

        private void TryAdd(Func<string, string> decoder, string value, int depth)
        {
            try
            {
                var decoded = decoder(value);
                if (!string.Equals(decoded, value, StringComparison.Ordinal) && decoded.Length <= _maxLength)
                {
                    Steps.Add(decoder == Uri.UnescapeDataString ? "URL decode" : "HTML entity decode");
                    AddRecursive(decoded, depth + 1);
                }
            }
            catch (Exception) when (decoder == Uri.UnescapeDataString)
            {
                // Invalid percent encoding is kept in its original form.
            }
        }

        private static bool TryDecodeBase64(string value, out string decoded)
        {
            decoded = "";
            var compact = value.Trim();
            if (compact.Length < 8 || compact.Length % 4 != 0 ||
                compact.Any(c => !(char.IsLetterOrDigit(c) || c is '+' or '/' or '='))) return false;
            try
            {
                var bytes = Convert.FromBase64String(compact);
                if (!LooksLikeText(bytes)) return false;
                decoded = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch (FormatException) { return false; }
        }

        private static bool TryDecodeHex(string value, out string decoded)
        {
            decoded = "";
            var compact = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
            if (compact.Length < 8 || compact.Length % 2 != 0 || !compact.All(Uri.IsHexDigit)) return false;
            try
            {
                var bytes = Convert.FromHexString(compact);
                if (!LooksLikeText(bytes)) return false;
                decoded = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch (FormatException) { return false; }
        }

        private static bool LooksLikeText(byte[] bytes)
        {
            if (bytes.Length == 0) return false;
            var printable = bytes.Count(b => b is 9 or 10 or 13 || b >= 32);
            return printable * 100 / bytes.Length >= 85;
        }
    }
}
