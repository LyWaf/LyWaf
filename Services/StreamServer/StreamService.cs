using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.StreamServer;

/// <summary>
/// TCP 流代理服务
/// 作为后台服务运行，监听 TCP 端口并转发到上游服务器
/// </summary>
public class StreamService : BackgroundService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private StreamServerOptions _options;
    private readonly List<(TcpListener listener, string key, StreamHandler handler)> _listeners = [];
    private readonly Dictionary<string, StreamHandler> _handlers = [];

    public StreamService(IOptionsMonitor<StreamServerOptions> optionsMonitor)
    {
        _options = optionsMonitor.CurrentValue;
        optionsMonitor.OnChange(newConfig =>
        {
            _options = newConfig;
            // 注意：热更新时不会自动重新创建监听器，需要重启服务
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                var listener = new TcpListener(host, port);
                listener.Start();

                var handler = new StreamHandler(_options, config, key);
                _listeners.Add((listener, key, handler));
                _handlers[key] = handler;

                var upstreams = string.Join(", ", config.Upstreams);
                _logger.Info("Stream 代理启动: {Host}:{Port} -> [{Upstreams}] ({Policy})", 
                    host, port, upstreams, config.Policy);

                // 启动接受连接的任务
                _ = AcceptConnectionsAsync(listener, key, handler, stoppingToken);
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

        // 停止所有监听器
        foreach (var (listener, _, _) in _listeners)
        {
            listener.Stop();
        }

        _logger.Info("Stream 代理服务已停止");
    }

    /// <summary>
    /// 解析端点配置
    /// 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:3306"
    /// </summary>
    private static (IPAddress host, int port)? ParseEndpoint(string key)
    {
        // 尝试解析 host:port 格式
        if (key.Contains(':'))
        {
            var lastColon = key.LastIndexOf(':');
            var hostPart = key[..lastColon];
            var portPart = key[(lastColon + 1)..];

            if (int.TryParse(portPart, out var port) && IPAddress.TryParse(hostPart, out var ip))
            {
                return (ip, port);
            }
        }
        // 尝试解析纯端口号
        else if (int.TryParse(key, out var port))
        {
            return (IPAddress.Any, port);
        }

        return null;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, string key, StreamHandler handler, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);

                // 异步处理连接
                _ = HandleClientAsync(client, handler, stoppingToken);
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

    private static async Task HandleClientAsync(TcpClient client, StreamHandler handler, CancellationToken stoppingToken)
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
        }
    }
}
