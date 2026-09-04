using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.RuntimeState;

/// <summary>
/// 将本地运行状态通过有界队列异步复制给对等节点，并周期性执行反熵广播以修复短暂断连造成的遗漏。
/// </summary>
public sealed class RuntimeStateReplicator : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeStateStore _store;
    private readonly IOptionsMonitor<RuntimeStateOptions> _options;
    private readonly Channel<RuntimeStateMutation> _queue;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public RuntimeStateReplicator(RuntimeStateStore store, IOptionsMonitor<RuntimeStateOptions> options)
    {
        _store = store;
        _options = options;
        _queue = Channel.CreateBounded<RuntimeStateMutation>(new BoundedChannelOptions(
            Math.Clamp(options.CurrentValue.QueueCapacity, 100, 1_000_000))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _store.MutationPublished += OnMutation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var consume = ConsumeAsync(stoppingToken);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var mutation in _store.ExportMutations()) _queue.Writer.TryWrite(mutation);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _queue.Writer.TryComplete();
            await consume;
        }
    }

    public override void Dispose()
    {
        _store.MutationPublished -= OnMutation;
        _client.Dispose();
        base.Dispose();
    }

    private void OnMutation(RuntimeStateMutation mutation)
    {
        if (_options.CurrentValue.Enabled) _queue.Writer.TryWrite(mutation);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var mutation in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                var current = _options.CurrentValue;
                if (!current.Enabled) continue;
                var body = JsonSerializer.SerializeToUtf8Bytes(mutation, JsonOptions);
                foreach (var pair in current.Peers.Where(x => x.Value.Enabled && x.Key != current.NodeId))
                {
                    try
                    {
                        var endpoint = pair.Value.Endpoint.TrimEnd('/') + "/api/runtime-state/agent/merge";
                        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                        {
                            Content = new ByteArrayContent(body)
                        };
                        request.Content.Headers.ContentType = new("application/json");
                        var secret = string.IsNullOrWhiteSpace(pair.Value.Secret) ? current.SharedSecret : pair.Value.Secret;
                        RuntimeStateAuthentication.Sign(request, current.NodeId, secret, body);
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (!response.IsSuccessStatusCode)
                            Logger.Warn("运行状态复制被节点拒绝: {Peer}, {Status}", pair.Key, (int)response.StatusCode);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
                    {
                        Logger.Warn(ex, "运行状态复制失败: {Peer}", pair.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
