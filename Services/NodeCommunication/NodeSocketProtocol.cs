using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeSocketRequest(string Operation, Dictionary<string, string[]> Headers, byte[] Body);
public sealed record NodeSocketResponse(int StatusCode, string ContentType, byte[] Body);

/// <summary>Adapts signed v2 HTTP messages without trusting identities supplied inside socket frames.</summary>
public sealed class NodeSocketProtocol(IServiceScopeFactory scopes)
{
    private static readonly object BootKey = new();
    internal static string? SessionBoot(HttpContext context) => context.Items.TryGetValue(BootKey, out var value) ? value as string : null;
    public static readonly string[] SignedHeaders = ["X-LyWaf-Cluster", "X-LyWaf-Sender", "X-LyWaf-Receiver",
        "X-LyWaf-KeyId", "X-LyWaf-Timestamp", "X-LyWaf-Nonce", "X-LyWaf-Signature"];

    public static async Task<byte[]> EncodeAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri?.AbsolutePath ?? throw new InvalidDataException("Missing node request path");
        if (request.Method != HttpMethod.Post || !path.StartsWith(NodeMessageAuthentication.Prefix, StringComparison.Ordinal)
            || request.RequestUri!.Query.Length != 0) throw new InvalidDataException("Invalid node request path");
        var operation = path[NodeMessageAuthentication.Prefix.Length..];
        if (!NodeProtocolOperations.IsSupported(operation)) throw new InvalidDataException("Unsupported node operation");
        var headers = SignedHeaders.ToDictionary(name => name, name => request.Headers.TryGetValues(name, out var values)
            ? values.ToArray() : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(token);
        return JsonSerializer.SerializeToUtf8Bytes(new NodeSocketRequest(operation, headers, body), NodeMessageAuthentication.JsonOptions);
    }

    public static HttpResponseMessage Decode(byte[] bytes)
    {
        var result = JsonSerializer.Deserialize<NodeSocketResponse>(bytes, NodeMessageAuthentication.JsonOptions)
            ?? throw new InvalidDataException("Missing socket response");
        if (result.StatusCode is < 100 or > 599 || result.Body is null || result.ContentType != "application/json")
            throw new InvalidDataException("Invalid socket response");
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)result.StatusCode) { Content = new ByteArrayContent(result.Body) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }

    public async Task<byte[]> DispatchAsync(byte[] bytes, string authenticatedPeer, bool secureTransport, CancellationToken token,
        string? authenticatedBootId = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var config = services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = token };
        if (authenticatedBootId is not null) context.Items[BootKey] = authenticatedBootId;
        using var output = new BoundedOutputStream(Math.Clamp(config.MaxMessageBytes, 1024, 16 * 1024 * 1024));
        context.Response.Body = output;
        IResult result;
        try
        {
            var request = JsonSerializer.Deserialize<NodeSocketRequest>(bytes, NodeMessageAuthentication.JsonOptions);
            if (request is null || request.Body is null || request.Body.LongLength > config.MaxMessageBytes
                || request.Headers is null || request.Headers.Count != SignedHeaders.Length
                || SignedHeaders.Any(name => !request.Headers.TryGetValue(name, out var values) || values is not { Length: 1 }
                    || values[0] is null || values[0].Length > 512)
                || request.Headers["X-LyWaf-Sender"][0] != authenticatedPeer)
                result = NodeProtocolResponse.Error(context, "node_identity_mismatch", 403);
            else
            {
                context.Request.Method = "POST";
                context.Request.Scheme = secureTransport ? "https" : "http";
                context.Request.Path = NodeMessageAuthentication.Prefix + request.Operation;
                context.Request.ContentLength = request.Body.Length;
                using var input = new MemoryStream(request.Body, writable: false);
                context.Request.Body = input;
                foreach (var name in SignedHeaders) context.Request.Headers[name] = request.Headers[name];
                if (request.Operation == LyWaf.Services.Statistic.CcExecutionQueryService.Operation)
                    LyWaf.Services.Statistic.CcQueryTiming.Capture(context);
                result = await NodeCommunicationEndpoints.DispatchAsync(context,
                    services.GetRequiredService<NodeMessageAuthentication>(), request.Operation);
            }
        }
        catch (JsonException) { result = NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
        await result.ExecuteAsync(context);
        return JsonSerializer.SerializeToUtf8Bytes(new NodeSocketResponse(context.Response.StatusCode,
            "application/json", output.ToArray()), NodeMessageAuthentication.JsonOptions);
    }

    private sealed class BoundedOutputStream(long maximum) : MemoryStream
    {
        private void Check(int count)
        {
            if (Position + count > maximum) throw new InvalidDataException("Node socket response exceeds limit");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { Check(count); return base.WriteAsync(buffer, offset, count, token); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, token); }
    }
}
