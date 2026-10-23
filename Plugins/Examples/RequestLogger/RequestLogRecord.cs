using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace LyWaf.Plugins.Examples.RequestLogger;

internal sealed record RequestLogRecord(DateTimeOffset Timestamp, string RequestId, string Method, string Path, string Query,
    string Host, string Scheme, string Protocol, string ClientIp, int Status, double? DurationMs,
    long? RequestBytes, long? ResponseBytes, string? Reason, bool ClientAborted, string? Failure,
    Dictionary<string, string>? Headers, string? RequestBody, string? ResponseBody,
    bool RequestBodyTruncated, bool ResponseBodyTruncated)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal string ToJsonLine() => JsonSerializer.Serialize(this, Json) + "\n";
    internal string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"========== [{Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}] ==========");
        text.AppendLine($"{Method} {Path}{Query} {Protocol}");
        text.AppendLine("Host: " + Host);
        text.AppendLine("Scheme: " + Scheme);
        text.AppendLine("Client-IP: " + ClientIp);
        text.AppendLine("Request-ID: " + RequestId);
        if (Reason is not null) text.AppendLine("Reason: " + Reason);
        text.AppendLine("Status: " + Status.ToString(CultureInfo.InvariantCulture)
            + (DurationMs is { } duration ? " | Duration: " + duration.ToString("F1", CultureInfo.InvariantCulture) + "ms" : ""));
        text.AppendLine($"Request-Bytes: {RequestBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"} | Response-Bytes: {ResponseBytes?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        if (ClientAborted) text.AppendLine("Client-Aborted: true");
        if (Failure is not null) text.AppendLine("Failure: " + Failure);
        if (Headers is not null)
        {
            text.AppendLine("--- Headers ---");
            foreach (var header in Headers) text.AppendLine(header.Key + ": " + header.Value);
        }
        if (RequestBody is not null) { text.AppendLine("--- Request Body ---"); text.AppendLine(RequestBody); }
        if (ResponseBody is not null) { text.AppendLine("--- Response Body ---"); text.AppendLine(ResponseBody); }
        if (RequestBodyTruncated || ResponseBodyTruncated) text.AppendLine("[Body capture truncated]");
        text.AppendLine();
        return text.ToString();
    }

    internal LyWaf.Utils.LogEntry ToEntry(int index, string raw) => new()
    {
        Index = index, Time = Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        Method = Method, Url = Path + Query, Host = Host, Scheme = Scheme, ClientIp = ClientIp, StatusCode = Status,
        Duration = DurationMs?.ToString("F1", CultureInfo.InvariantCulture) + (DurationMs is null ? "" : "ms"), Reason = Reason,
        RequestLine = $"{Method} {Path}{Query} {Protocol}", RequestBody = RequestBody, ResponseBody = ResponseBody,
        Headers = Headers is null ? null : string.Join('\n', Headers.Select(pair => pair.Key + ": " + pair.Value)), Raw = raw
    };
}

internal sealed class RequestLogRedactor(IEnumerable<string>? extraNames)
{
    private readonly HashSet<string> sensitive = new(new[]
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "password", "passwd", "pwd", "secret", "key",
        "token", "access_token", "refresh_token", "id_token", "api_key", "apikey", "x-api-key", "x-auth-token", "client_secret", "signature"
    }.Concat(extraNames ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase);
    private static string Normalize(string value) => new(value.Where(char.IsAsciiLetterOrDigit).ToArray());
    internal bool IsSensitive(string name) => sensitive.Contains(Normalize(name));

    internal static string Line(string? value, int maxBytes = 4096)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var part = Rune.IsControl(rune) ? "\\u" + rune.Value.ToString("x4", CultureInfo.InvariantCulture) : rune.ToString();
            bytes += Encoding.UTF8.GetByteCount(part);
            if (bytes > maxBytes) { result.Append("[truncated]"); break; }
            result.Append(part);
        }
        return result.ToString();
    }

    internal string Query(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        var pairs = QueryHelpers.ParseQuery(query).Take(128).SelectMany(pair => pair.Value.Select(value =>
            new KeyValuePair<string, string?>(Line(pair.Key, 256), IsSensitive(pair.Key) ? "[REDACTED]" : Line(value, 2048))));
        return QueryString.Create(pairs).Value ?? "";
    }

    internal string? Body(string? body, string? contentType, int maxBytes, bool truncated)
    {
        if (string.IsNullOrEmpty(body)) return null;
        if (truncated) return "[Body omitted: capture limit exceeded]";
        var type = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        if (type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal))
        {
            try
            {
                var node = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
                Redact(node);
                var rendered = node?.ToJsonString() ?? "null";
                return Encoding.UTF8.GetByteCount(rendered) <= maxBytes ? rendered : "[Body omitted: encoded size limit exceeded]";
            }
            catch (JsonException) { return "[Body omitted: invalid JSON]"; }
        }
        if (type == "application/x-www-form-urlencoded") return Query(body).TrimStart('?');
        return "[Body omitted: unsupported or binary content type]";
    }

    private void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
                if (IsSensitive(pair.Key)) obj[pair.Key] = "[REDACTED]"; else Redact(pair.Value);
        else if (node is JsonArray array) foreach (var item in array) Redact(item);
    }
}
