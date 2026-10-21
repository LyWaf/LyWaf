using System.Threading.Channels;

namespace LyWaf.Services.ResponseCaching;

internal sealed record ResponseCacheRefreshRequest(string CacheKey, Uri Uri, IReadOnlyDictionary<string, string> VaryHeaders);

/// <summary>有界异步刷新队列；同一 URL 在任务完成前只允许一个后台请求。</summary>
public sealed class ResponseCacheRefreshService(IHttpClientFactory clientFactory) : BackgroundService
{
    private readonly Channel<ResponseCacheRefreshRequest> _queue = Channel.CreateBounded<ResponseCacheRefreshRequest>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal bool TryQueue(ResponseCacheRefreshRequest request)
    {
        lock (_gate)
        {
            if (!_pending.Add(request.CacheKey)) return false;
            if (_queue.Writer.TryWrite(request)) return true;
            _pending.Remove(request.CacheKey);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, item.Uri);
                    request.Headers.TryAddWithoutValidation("X-LyWaf-Cache-Refresh", "1");
                    foreach (var pair in item.VaryHeaders)
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    using var response = await clientFactory.CreateClient("response-cache-refresh")
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                }
                catch when (!stoppingToken.IsCancellationRequested) { }
                finally
                {
                    lock (_gate) _pending.Remove(item.CacheKey);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { }
            lock (_gate) _pending.Clear();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}
