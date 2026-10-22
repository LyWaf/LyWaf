using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

/// <summary>Multiplexes bounded RPCs over an already authenticated socket. Delivery is not an acknowledgement.</summary>
public sealed class NodeSocketChannel : IAsyncDisposable
{
    private sealed record Frame(string Id, bool Reply, byte[] Body, string? Error = null);
    private readonly WebSocket socket;
    private readonly int maxBytes;
    private readonly SemaphoreSlim send = new(1, 1);
    private readonly SemaphoreSlim outgoing;
    private readonly SemaphoreSlim incoming;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pending = new();
    private readonly ConcurrentDictionary<string, Task> handlers = new();
    private int started;

    public NodeSocketChannel(WebSocket socket, int maxMessageBytes = 1024 * 1024, int maxConcurrentRequests = 32)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (maxMessageBytes is < 1024 or > 40 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        if (maxConcurrentRequests is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
        this.socket = socket;
        maxBytes = maxMessageBytes;
        outgoing = new(maxConcurrentRequests, maxConcurrentRequests);
        incoming = new(maxConcurrentRequests, maxConcurrentRequests);
    }

    public async Task<byte[]> RequestAsync(byte[] body, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired = false;
        try
        {
            if (!await outgoing.WaitAsync(0, linked.Token)) throw new IOException("Node socket request capacity exceeded");
            acquired = true;
            pending[id] = completion;
            await SendAsync(new(id, false, body), linked.Token);
            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            throw new IOException("Node socket disconnected; delivery outcome may be unknown");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new IOException("Node socket disconnected; delivery outcome may be unknown", ex);
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (acquired) outgoing.Release();
        }
    }

    public async Task RunAsync(Func<byte[], CancellationToken, Task<byte[]>> handle, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Socket receive loop already started");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        var buffer = new byte[Math.Min(16 * 1024, maxBytes)];
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Binary || message.Length + part.Count > maxBytes)
                        throw new InvalidDataException("Invalid node socket frame type or size");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                var frame = JsonSerializer.Deserialize<Frame>(message.GetBuffer().AsSpan(0, (int)message.Length))
                    ?? throw new InvalidDataException("Missing node socket frame");
                if (!Guid.TryParseExact(frame.Id, "N", out _) || frame.Body is null)
                    throw new InvalidDataException("Invalid node socket frame");
                if (frame.Reply)
                {
                    if (pending.TryRemove(frame.Id, out var result))
                    {
                        if (frame.Error is null) result.TrySetResult(frame.Body);
                        else result.TrySetException(new IOException("Remote node socket request failed: " + frame.Error));
                    }
                    continue;
                }
                if (frame.Error is not null) throw new InvalidDataException("Unexpected request error");
                if (!await incoming.WaitAsync(0, token))
                {
                    await SendAsync(new(frame.Id, true, [], "busy"), token);
                    continue;
                }
                // Receive stays available for replies even while a request handler makes a nested RPC.
                var task = DispatchAsync(frame, handle, token);
                var key = Guid.NewGuid().ToString("N");
                handlers[key] = task;
                _ = task.ContinueWith(_ => { handlers.TryRemove(key, out var ignored); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            socket.Abort();
            foreach (var item in pending.Values) item.TrySetException(new IOException("Node socket disconnected; delivery outcome may be unknown"));
            await Task.WhenAll(handlers.Values);
        }
    }

    private async Task DispatchAsync(Frame frame, Func<byte[], CancellationToken, Task<byte[]>> handle, CancellationToken token)
    {
        try
        {
            byte[] body;
            try { body = await handle(frame.Body, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception) { await SendAsync(new(frame.Id, true, [], "handler_failed"), token); return; }
            await SendAsync(new(frame.Id, true, body), token);
        }
        catch (Exception) { socket.Abort(); await lifetime.CancelAsync(); }
        finally { incoming.Release(); }
    }

    private async Task SendAsync(Frame frame, CancellationToken token)
    {
        if (frame.Body.Length > maxBytes) throw new InvalidDataException("Node socket payload exceeds limit");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (bytes.Length > maxBytes) throw new InvalidDataException("Node socket frame exceeds limit");
        await send.WaitAsync(token);
        try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, token); }
        finally { send.Release(); }
    }

    internal void Abort()
    {
        lifetime.Cancel();
        try { socket.Abort(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        Abort();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
