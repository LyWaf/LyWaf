using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

/// <summary>Reads bounded protocol metadata without retaining untrusted response text.</summary>
public static class NodeRemoteError
{
    public const int MaximumBodyBytes = 8192;

    public static async Task<HttpRequestException> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        string? code = null, traceId = null;
        try
        {
            if (response.Content.Headers.ContentLength is null or <= MaximumBodyBytes)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var bytes = new byte[MaximumBodyBytes + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(length), token);
                    if (read == 0) break;
                    length += read;
                }
                if (length is > 0 and <= MaximumBodyBytes)
                {
                    using var body = JsonDocument.Parse(bytes.AsMemory(0, length), new() { MaxDepth = 8 });
                    if (body.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        code = ReadIdentifier(body.RootElement, "errorCode");
                        traceId = ReadIdentifier(body.RootElement, "traceId");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            // The HTTP failure remains authoritative if its optional metadata cannot be read.
        }

        var error = new HttpRequestException(code is null ? "Node request rejected" : $"Node request rejected ({code})",
            null, response.StatusCode);
        if (code is not null)
        {
            error.Data["ErrorCode"] = code;
            error.Data["MessageKey"] = "node.errors." + code;
        }
        if (traceId is not null) error.Data["RemoteTraceId"] = traceId;
        if (response.Headers.RetryAfter is { } retry)
            error.Data["RetryAfter"] = retry.Delta ?? (retry.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
        return error;
    }

    private static string? ReadIdentifier(JsonElement body, string name)
    {
        var properties = body.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.String) return null;
        var value = properties[0].Value.GetString();
        return value is { Length: > 0 and <= 128 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') ? value : null;
    }
}
