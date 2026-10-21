using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Services.ManagementApi;

public sealed record ManagementNonReplayableResponse;

/// <summary>有限幂等记录，保存请求摘要及最多 64 KiB 响应；超限结果不允许重复执行。</summary>
public sealed class ManagementIdempotency
{
    internal sealed record Response(int Status, string? ContentType, byte[]? Body, string? Location);
    internal sealed class Entry(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<Response> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal (Entry? Entry, bool Owner, bool Conflict) Acquire(string key, string fingerprint)
    {
        lock (gate)
        {
            foreach (var old in entries.Where(e => e.Value.Completion.Task.IsCompleted && e.Value.Created < DateTimeOffset.UtcNow.AddMinutes(-10)).Select(e => e.Key).ToArray()) entries.Remove(old);
            if (entries.TryGetValue(key, out var existing)) return (existing, false, existing.Fingerprint != fingerprint);
            if (entries.Count >= 128) return (null, false, false);
            var entry = new Entry(fingerprint);
            entries.Add(key, entry);
            return (entry, true, false);
        }
    }
}

public static class ManagementIdempotencyMiddleware
{
    public static void UseManagementIdempotency(this WebApplication app, int port)
    {
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            if (context.Connection.LocalPort != port || method is not ("POST" or "PUT" or "PATCH" or "DELETE") ||
                !context.Request.Path.StartsWithSegments("/api") || IsSessionOperation(context.Request.Path) ||
                context.Items["Username"] is not string)
            { await next(); return; }
            var keys = context.Request.Headers["Idempotency-Key"];
            var key = keys.FirstOrDefault();
            if (key is null) { await next(); return; }
            if (keys.Count != 1 || key.Length is < 8 or > 100 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            {
                await ManagementApiContract.Error(context, 400, "invalid_idempotency_key", "幂等键必须是 8-100 位字母、数字或短横线").ExecuteAsync(context);
                return;
            }
            context.Request.EnableBuffering(65536);
            var position = context.Request.Body.Position;
            var bodyHash = await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted);
            context.Request.Body.Position = position;
            var identity = context.Items["Username"]?.ToString() ?? "unknown";
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                method + "\n" + context.Request.Path + context.Request.QueryString + "\n" + context.Request.ContentType + "\n" + Convert.ToHexString(bodyHash))));
            var store = context.RequestServices.GetRequiredService<ManagementIdempotency>();
            var credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString())));
            var (entry, owner, conflict) = store.Acquire(identity + "\n" + credential + "\n" + key, fingerprint);
            if (entry == null || conflict)
            {
                await ManagementApiContract.Error(context, entry == null ? 503 : 409,
                    entry == null ? "idempotency_capacity" : "idempotency_conflict", entry == null ? "幂等记录容量已满，请稍后重试" : "该幂等键已用于不同请求").ExecuteAsync(context);
                return;
            }
            if (!owner)
            {
                var response = await entry.Completion.Task.WaitAsync(context.RequestAborted);
                if (response.Body is null)
                {
                    await ManagementApiContract.Error(context, 409, "idempotency_unreplayable", "原请求已执行但响应无法重放，请查询操作状态").ExecuteAsync(context);
                    return;
                }
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = response.ContentType;
                context.Response.Headers["Idempotency-Replayed"] = "true";
                if (response.Location != null) context.Response.Headers.Location = response.Location;
                await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
                return;
            }
            var original = context.Response.Body;
            using var capture = new BoundedResponseCapture(original);
            context.Response.Body = capture;
            try
            {
                await next();
                entry.Completion.TrySetResult(new(context.Response.StatusCode, context.Response.ContentType,
                    context.GetEndpoint()?.Metadata.GetMetadata<ManagementNonReplayableResponse>() is not null ? null : capture.Bytes,
                    context.Response.Headers.Location.FirstOrDefault()));
            }
            catch
            {
                entry.Completion.TrySetResult(new(500, null, null, null));
                throw;
            }
            finally { context.Response.Body = original; }
        });
    }

    // Session proofs and one-time codes must never be cached as reusable responses.
    private static bool IsSessionOperation(PathString path) =>
        path.StartsWithSegments("/api/auth/mfa") || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase);

    private sealed class BoundedResponseCapture(Stream inner) : Stream
    {
        private readonly MemoryStream capture = new();
        private bool exceeded;
        public byte[]? Bytes => exceeded ? null : capture.ToArray();
        private void Remember(ReadOnlySpan<byte> buffer)
        {
            if (exceeded) return;
            if (capture.Length + buffer.Length > 65536) { exceeded = true; capture.SetLength(0); return; }
            capture.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count) { Remember(buffer.AsSpan(offset, count)); inner.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Remember(buffer.Span); await inner.WriteAsync(buffer, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) capture.Dispose(); base.Dispose(disposing); }
    }
}
