namespace LyWaf.Services.Lifecycle;

/// <summary>
/// 平滑停止的可观察状态，供控制台和外部进程管理器判断实例是否仍可接流量。
/// </summary>
public sealed record DrainSnapshot(
    bool Ready,
    bool Draining,
    long ActiveRequests,
    long ActiveWebSockets,
    DateTimeOffset? DrainStartedAt);

/// <summary>
/// 协调实例就绪状态、停止接收新代理请求以及等待在途请求排空。
/// </summary>
public sealed class GracefulShutdownCoordinator
{
    private readonly object _sync = new();
    private TaskCompletionSource _drained = CompletedSource();
    private long _activeRequests;
    private long _activeWebSockets;
    private int _ready;
    private int _draining;
    private DateTimeOffset? _drainStartedAt;

    /// <summary>实例完成启动并可接收流量时调用。</summary>
    public void MarkReady() => Volatile.Write(ref _ready, 1);

    /// <summary>进入排空状态；此后新的代理请求应被拒绝。</summary>
    public void BeginDrain()
    {
        if (Interlocked.Exchange(ref _draining, 1) != 0) return;
        Volatile.Write(ref _ready, 0);
        _drainStartedAt = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (Interlocked.Read(ref _activeRequests) > 0)
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>尝试登记一个新请求；排空开始后返回 false。</summary>
    public bool TryEnter(bool webSocket)
    {
        if (Volatile.Read(ref _draining) != 0) return false;
        Interlocked.Increment(ref _activeRequests);
        if (webSocket) Interlocked.Increment(ref _activeWebSockets);
        if (Volatile.Read(ref _draining) == 0) return true;
        Exit(webSocket);
        return false;
    }

    /// <summary>标记请求完成，并在最后一个请求退出时唤醒排空等待者。</summary>
    public void Exit(bool webSocket)
    {
        if (webSocket) Interlocked.Decrement(ref _activeWebSockets);
        if (Interlocked.Decrement(ref _activeRequests) == 0)
        {
            lock (_sync) _drained.TrySetResult();
        }
    }

    /// <summary>等待全部在途请求完成；超时返回 false。</summary>
    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Read(ref _activeRequests) == 0) return true;
        Task drained;
        lock (_sync) drained = _drained.Task;
        try
        {
            await drained.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    /// <summary>读取当前生命周期状态。</summary>
    public DrainSnapshot Snapshot() => new(
        Volatile.Read(ref _ready) != 0,
        Volatile.Read(ref _draining) != 0,
        Interlocked.Read(ref _activeRequests),
        Interlocked.Read(ref _activeWebSockets),
        _drainStartedAt);

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

/// <summary>
/// 位于代理管线最外层，统计 HTTP、下载和 WebSocket 的完整请求生命周期。
/// </summary>
public sealed class RequestDrainMiddleware(RequestDelegate next, GracefulShutdownCoordinator coordinator)
{
    /// <summary>拒绝排空期新请求，并确保已登记请求最终退出计数。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var webSocket = context.WebSockets.IsWebSocketRequest;
        if (!coordinator.TryEnter(webSocket))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsync("Service is draining");
            return;
        }

        try { await next(context); }
        finally { coordinator.Exit(webSocket); }
    }
}
