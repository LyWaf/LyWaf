using System.Diagnostics;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using LyWaf.Utils;
using NLog;
namespace LyWaf.Middleware;

public class ThrottledMiddleware(RequestDelegate next, ISpeedLimitService speedService, IStatisticService statisticService)
{
    private readonly RequestDelegate _next = next;
    private readonly ISpeedLimitService speedService = speedService;
    private readonly IStatisticService statisticService = statisticService;

    public async Task Invoke(HttpContext context)
    {
        var option = speedService.GetOptions();
        if (!option.Throttled.Enabled)
        {
            await _next(context);
            return;
        }
        // 使用合并后的配置（静态配置 + 动态规则）
        var config = speedService.GetThrottleConfig();
        var clientIp = RequestUtil.GetClientIp(context.Request);
        var path = await statisticService.GetMatchPath(context.Request.Path);
        var originalBody = context.Response.Body;
        Stream? wrappedBody = null;

        if (config.PathLimits.TryGetValue(path, out var val))
        {
            wrappedBody = new UrlThrottledStream(originalBody, val * 1024);
        }
        else if (config.IpLimits.TryGetValue(clientIp, out val))
        {
            wrappedBody = new IpThrottledStream(originalBody, clientIp, val * 1024);
        }
        else if (config.Global != 0)
        {
            wrappedBody = new UrlThrottledStream(originalBody, config.Global * 1024);
        }

        if (wrappedBody != null)
        {
            context.Response.Body = wrappedBody;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            // 还原原始 Body，确保 ASP.NET 能正常管理生命周期
            if (wrappedBody != null)
            {
                context.Response.Body = originalBody;
            }
        }
    }
}

public class UrlThrottledStream(Stream inner, int bytesPerSecond) : Stream
{
    private readonly Stream _inner = inner;
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var totalLength = buffer.Length;
        if (totalLength == 0) return;

        var totalChunks = (int)Math.Ceiling(totalLength / (double)bytesPerSecond);

        var stopwatch = Stopwatch.StartNew();
        var totalSent = 0L;

        try
        {
            for (int i = 0; i < totalChunks; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                int nowOffset = i * bytesPerSecond;
                int length = Math.Min(bytesPerSecond, buffer.Length - nowOffset);

                // 计算应该发送的时间
                var targetTime = TimeSpan.FromSeconds((double)totalSent / bytesPerSecond);
                var actualTime = stopwatch.Elapsed;

                if (targetTime > actualTime)
                {
                    var delay = targetTime - actualTime;
                    await Task.Delay(delay, cancellationToken);
                }

                await _inner.WriteAsync(buffer.Slice(nowOffset, length), cancellationToken);
                totalSent += length;

                _logger.Trace("Sent url {TotalSent}/{TotalLength} bytes ({Percentage:F1}%)", totalSent, totalLength, (double)totalSent / totalLength * 100);
            }
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        // 不要 dispose inner stream，由 ASP.NET 管理
    }

    public override ValueTask DisposeAsync()
    {
        // 不要 dispose inner stream，由 ASP.NET 管理
        return ValueTask.CompletedTask;
    }
}

public class IpThrottledStream(Stream inner, string clientIp, int bytesPerSecond) : Stream
{
    private readonly Stream _inner = inner;
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        var left = buffer.Length;
        if (left == 0) return;

        try
        {
            while (left > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                var nowAlloc = 0;
                SharedData.ClientThrottled.DoLockKeyFunc(clientIp, (_) => new ClientThrottledLimit
                {
                    EveryCapacity = bytesPerSecond,
                    LeftToken = bytesPerSecond,
                }, (val) =>
                {
                    nowAlloc = val.AllocToken(left);
                    return true;
                });

                if (nowAlloc == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    continue;
                }

                await _inner.WriteAsync(buffer.Slice(offset, nowAlloc), cancellationToken);
                offset += nowAlloc;
                left -= nowAlloc;

                _logger.Trace("Sent {ClientIp} {Offset}/{BufferLength} bytes ({Percentage:F1}%)", clientIp, offset, buffer.Length, (double)offset / buffer.Length * 100);
            }
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        // 不要 dispose inner stream，由 ASP.NET 管理
    }

    public override ValueTask DisposeAsync()
    {
        // 不要 dispose inner stream，由 ASP.NET 管理
        return ValueTask.CompletedTask;
    }
}