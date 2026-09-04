using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NLog;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.RequestMirroring;

/// <summary>通过有界队列异步发送镜像请求；镜像响应和错误始终与主请求隔离。</summary>
public sealed class RequestMirrorDispatcher : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Channel<MirroredRequest> _queue;
    private readonly IHttpClientFactory _clients;
    private readonly RequestMirroringOptions _options;
    private readonly PrometheusMetrics _metrics;

    public RequestMirrorDispatcher(IHttpClientFactory clients, IOptions<RequestMirroringOptions> options,
        PrometheusMetrics metrics)
    {
        _clients = clients;
        _options = options.Value;
        _metrics = metrics;
        _queue = Channel.CreateBounded<MirroredRequest>(new BoundedChannelOptions(
            Math.Clamp(_options.QueueCapacity, 1, 10_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = Math.Clamp(_options.Workers, 1, 32) == 1
        });
    }

    /// <summary>尝试加入后台队列；队列满或服务停止时立即返回 false。</summary>
    public bool TryEnqueue(MirroredRequest request)
    {
        var queued = _queue.Writer.TryWrite(request);
        _metrics.RecordMirror(queued ? "queued" : "queue_full");
        return queued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Clamp(_options.Workers, 1, 32))
            .Select(_ => RunWorkerAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var request = new HttpRequestMessage(item.Method, item.Target);
                foreach (var (name, values) in item.Headers)
                    request.Headers.TryAddWithoutValidation(name, values);
                if (item.Body != null)
                {
                    request.Content = new ByteArrayContent(item.Body);
                    if (!string.IsNullOrWhiteSpace(item.ContentType))
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", item.ContentType);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300)));
                using var response = await _clients.CreateClient("request-mirror")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                _metrics.RecordMirror(response.IsSuccessStatusCode ? "success" : "failure_status");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _metrics.RecordMirror("timeout");
                Logger.Debug("镜像请求超时: {Target}", item.Target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _metrics.RecordMirror("failure");
                Logger.Debug(ex, "镜像请求失败: {Target}", item.Target);
            }
        }
    }
}
