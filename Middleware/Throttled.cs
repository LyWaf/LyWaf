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
        var clientIp = RequestUtil.GetClientIp(context.Request);
        var path = await statisticService.GetMatchPath(context.Request.Path);
        var body = context.Response.Body;

        if (option.Throttled.Everys.TryGetValue(path, out var val))
        {
            body = new UrlThrottledStream(body, val * 1024);
        }
        else if (option.Throttled.Global != 0)
        {
            body = new UrlThrottledStream(body, option.Throttled.Global * 1024);
        }

        if (option.Throttled.IpEverys.TryGetValue(clientIp, out val))
        {
            body = new IpThrottledStream(body, clientIp, val * 1024);
        }
        context.Response.Body = body;
        await _next(context);

        if (body is UrlThrottledStream || body is IpThrottledStream)
        {
            await body.DisposeAsync();
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
        var totalChunks = (int)Math.Ceiling(totalLength / (double)bytesPerSecond);

        var stopwatch = Stopwatch.StartNew();
        var totalSent = 0L;

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

            _logger.Info("Sent url {TotalSent}/{TotalLength} bytes ({Percentage:F1}%)", totalSent, totalLength, (double)totalSent / totalLength * 100);
        }
    }

    // ... 其他必要的 Stream 成员，通常直接委托给 _inner
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
}

public class IpThrottledStream(Stream inner, string clientIp, int bytesPerSecond) : Stream
{
    private readonly Stream _inner = inner;
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        var left = buffer.Length;
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
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                continue;
            }

            await _inner.WriteAsync(buffer.Slice(offset, nowAlloc), cancellationToken);
            offset += nowAlloc;
            left -= nowAlloc;

            _logger.Info("Sent {ClientIp} {Offset}/{BufferLength} bytes ({Percentage:F1}%)", clientIp, offset, buffer.Length, (double)offset / buffer.Length * 100);
        }
    }

    // ... 其他必要的 Stream 成员，通常直接委托给 _inner
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
}