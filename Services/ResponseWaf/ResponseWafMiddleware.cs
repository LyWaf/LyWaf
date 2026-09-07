using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.ResponseWaf;

public sealed class ResponseWafMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ResponseWafOptions> options,
    IResponseWafDetector detector)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task InvokeAsync(HttpContext context)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || context.WebSockets.IsWebSocketRequest || HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        await using var capture = new BoundedResponseCaptureStream(context, original,
            Math.Clamp(current.MaxInspectBytes, 1024, 4 * 1024 * 1024));
        context.Response.Body = capture;
        try { await next(context); }
        finally { context.Response.Body = original; }

        if (capture.Bypassed) return;
        var raw = capture.GetBufferedBytes();
        var decoded = DecodeForInspection(raw, context.Response.Headers.ContentEncoding.ToString(), current.MaxInspectBytes);
        if (decoded.Truncated)
        {
            await original.WriteAsync(raw, context.RequestAborted);
            return;
        }
        var body = IsTextContent(context.Response.ContentType) ? Encoding.UTF8.GetString(decoded.Bytes) : null;
        var finding = detector.Detect(context.Response.StatusCode, context.Response.Headers, body);
        if (finding is null)
        {
            await original.WriteAsync(raw, context.RequestAborted);
            return;
        }

        context.Items["ResponseWaf.Finding"] = finding;
        Logger.Warn("响应 WAF 命中: Rule={Rule}, Category={Category}, Field={Field}, Path={Path}",
            finding.RuleId, finding.Category, finding.Field, context.Request.Path);
        var action = current.Action;
        if (action.Equals("Block", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = Math.Clamp(current.BlockStatusCode, 400, 599);
            context.Response.Headers.Remove("Content-Encoding");
            context.Response.Headers.Remove("Content-Length");
            context.Response.Headers.Remove("ETag");
            context.Response.ContentType = "application/json; charset=utf-8";
            var blocked = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
            {
                error = "response_waf_blocked",
                message = current.BlockMessage,
                ruleId = finding.RuleId
            }));
            context.Response.ContentLength = blocked.Length;
            await original.WriteAsync(blocked, context.RequestAborted);
            return;
        }
        if (action.Equals("Redact", StringComparison.OrdinalIgnoreCase) && body is not null &&
            string.IsNullOrWhiteSpace(context.Response.Headers.ContentEncoding))
        {
            var redacted = Encoding.UTF8.GetBytes(detector.Redact(body));
            context.Response.ContentLength = redacted.Length;
            context.Response.Headers.Remove("ETag");
            await original.WriteAsync(redacted, context.RequestAborted);
            return;
        }
        await original.WriteAsync(raw, context.RequestAborted);
    }

    private static bool IsTextContent(string? contentType) => contentType is not null &&
        (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
         contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase));

    private static (byte[] Bytes, bool Truncated) DecodeForInspection(byte[] source, string encoding, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(encoding)) return (source, false);
        try
        {
            using var input = new MemoryStream(source, writable: false);
            using Stream decoder = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => Stream.Null
            };
            if (ReferenceEquals(decoder, Stream.Null)) return (source, true);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (output.Length <= maxBytes)
            {
                var read = decoder.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length));
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            return (output.GetBuffer().AsSpan(0, Math.Min((int)output.Length, maxBytes)).ToArray(), output.Length > maxBytes);
        }
        catch (InvalidDataException) { return (source, true); }
    }

    private sealed class BoundedResponseCaptureStream(HttpContext context, Stream destination, int maxBytes) : Stream
    {
        private readonly MemoryStream _buffer = new(Math.Min(maxBytes, 64 * 1024));
        public bool Bypassed { get; private set; }
        public byte[] GetBufferedBytes() => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush()
        {
            if (IsStreaming()) BypassAsync(default).GetAwaiter().GetResult();
        }
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            IsStreaming() ? BypassAsync(cancellationToken).AsTask() : Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Bypassed) { await destination.WriteAsync(buffer, cancellationToken); return; }
            if (_buffer.Length + buffer.Length <= maxBytes) { await _buffer.WriteAsync(buffer, cancellationToken); return; }
            await BypassAsync(cancellationToken);
            await destination.WriteAsync(buffer, cancellationToken);
        }
        private async ValueTask BypassAsync(CancellationToken cancellationToken)
        {
            if (Bypassed) return;
            Bypassed = true;
            _buffer.Position = 0;
            await _buffer.CopyToAsync(destination, cancellationToken);
            _buffer.SetLength(0);
        }
        private bool IsStreaming() =>
            context.WebSockets.IsWebSocketRequest ||
            context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true ||
            context.Response.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }
}
