using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NLog;
using LyWaf.Services.Metrics;
using System.Collections.Concurrent;

namespace LyWaf.Services.StreamServer;

/// <summary>
/// TCP/UDP 四层代理服务，负责监听、会话准入及上游转发。
/// </summary>
public class StreamService : BackgroundService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private StreamServerOptions _options;
    private readonly List<(TcpListener listener, string key, StreamHandler handler)> _listeners = [];
    private readonly List<UdpStreamProxy> _udpProxies = [];
    private readonly List<Task> _runningTasks = [];
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private long _connectionId;
    private StreamHealthChecker? _healthChecker;
    private readonly PrometheusMetrics? _metrics;

    public StreamService(IOptionsMonitor<StreamServerOptions> optionsMonitor, PrometheusMetrics? metrics = null)
    {
        _metrics = metrics;
        _options = optionsMonitor.CurrentValue;
        optionsMonitor.OnChange(newConfig =>
        {
            _options = newConfig;
            // 注意：热更新时不会自动重新创建监听器，需要重启服务
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 立即让出控制权，避免后续同步工作（健康检查器启动、监听器绑定）阻塞 host 启动就绪
        await Task.Yield();

        if (!_options.Enabled)
        {
            _logger.Info("Stream 代理服务未启用");
            return;
        }

        // 查找启用的流配置
        var enabledStreams = _options.Streams
            .Where(s => s.Value.Enabled && s.Value.Upstreams.Count > 0)
            .ToList();

        if (enabledStreams.Count == 0)
        {
            _logger.Info("没有配置 Stream 代理");
            return;
        }

        // 创建并启动健康检查器
        _healthChecker = new StreamHealthChecker(_options);
        _healthChecker.Start();

        // 启动监听器
        foreach (var (key, config) in enabledStreams)
        {
            var endpoint = ParseEndpoint(key);
            if (!endpoint.HasValue)
            {
                _logger.Warn("Stream 配置 {Key} 的监听地址格式无效", key);
                continue;
            }

            var (host, port) = endpoint.Value;

            try
            {
                var upstreams = string.Join(", ", config.Upstreams);
                if (config.Protocol == StreamProtocol.Udp)
                {
                    var proxy = new UdpStreamProxy(new IPEndPoint(host, port), key, config, _healthChecker, _metrics);
                    _udpProxies.Add(proxy);
                    _runningTasks.Add(proxy.RunAsync(stoppingToken));
                }
                else
                {
                    var listener = new TcpListener(host, port);
                    listener.Start();
                    var handler = new StreamHandler(_options, config, key, _healthChecker, _metrics);
                    _listeners.Add((listener, key, handler));
                    _runningTasks.Add(AcceptConnectionsAsync(listener, key, handler, config, stoppingToken));
                }
                _logger.Info("Stream {Protocol} 代理启动: {Host}:{Port} -> [{Upstreams}] ({Policy})",
                    config.Protocol, host, port, upstreams, config.Policy);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stream 代理启动失败: {Key}", key);
            }
        }

        // 等待取消
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // 正常停止
        }

        // 停止健康检查器
        _healthChecker?.Stop();
        _healthChecker?.Dispose();

        // 停止所有监听器
        foreach (var (listener, _, _) in _listeners)
        {
            listener.Stop();
        }
        foreach (var proxy in _udpProxies) proxy.Dispose();
        try { await Task.WhenAll(_runningTasks); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(_connections.Values); } catch (OperationCanceledException) { }

        _logger.Info("Stream 代理服务已停止");
    }

    /// <summary>
    /// 解析端点配置
    /// 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:3306", "[::1]:8443"
    /// </summary>
    private static (IPAddress host, int port)? ParseEndpoint(string key)
    {
        if (int.TryParse(key, out var port) && port is >= 1 and <= 65535)
            return (IPAddress.Any, port);
        return StreamPrimitives.TryParseEndpoint(key, out var endpoint) &&
               IPAddress.TryParse(endpoint!.Host, out var address)
            ? (address, endpoint.Port)
            : null;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, string key, StreamHandler handler,
        StreamConfig config, CancellationToken stoppingToken)
    {
        var connections = new ConnectionCounter(config.MaxConnections);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (!connections.TryEnter())
                {
                    _metrics?.RecordStreamEvent(key, "tcp", "rejected");
                    client.Dispose();
                    continue;
                }
                _metrics?.RecordStreamEvent(key, "tcp", "accepted");
                var id = Interlocked.Increment(ref _connectionId);
                var task = HandleClientAsync(client, key, handler, connections, stoppingToken);
                _connections[id] = task;
                _ = task.ContinueWith(completed => _connections.TryRemove(id, out var removed), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stream {Key} 接受连接失败", key);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, string key, StreamHandler handler,
        ConnectionCounter connections, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                await handler.HandleAsync(client.Client, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stream 处理连接失败");
            }
            finally
            {
                connections.Exit();
                _metrics?.RecordStreamEvent(key, "tcp", "closed");
            }
        }
    }

    private sealed class ConnectionCounter(int limit)
    {
        private int _active;
        public bool TryEnter()
        {
            var value = Interlocked.Increment(ref _active);
            if (limit <= 0 || value <= limit) return true;
            Interlocked.Decrement(ref _active);
            return false;
        }
        public void Exit() => Interlocked.Decrement(ref _active);
    }
}
