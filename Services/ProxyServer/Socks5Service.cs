using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// SOCKS5 代理服务
/// 作为后台服务运行，监听 SOCKS5 代理请求
/// </summary>
public class Socks5Service : BackgroundService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private ProxyServerOptions _options;
    private readonly List<(TcpListener listener, string key, IPAddress host, int port)> _listeners = [];

    public Socks5Service(IOptionsMonitor<ProxyServerOptions> optionsMonitor)
    {
        _options = optionsMonitor.CurrentValue;
        optionsMonitor.OnChange(newConfig =>
        {
            _options = newConfig;
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Info("SOCKS5 代理服务未启用");
            return;
        }

        // 查找启用了 SOCKS5 的端口配置
        // 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:1080"
        var socks5Endpoints = _options.Ports
            .Where(p => p.Value.EnableSocks5)
            .Select(p => ParseEndpoint(p.Key))
            .Where(e => e.HasValue)
            .Select(e => e!.Value)
            .ToList();

        if (socks5Endpoints.Count == 0)
        {
            _logger.Info("没有配置 SOCKS5 代理端口");
            return;
        }

        // 启动监听器
        foreach (var (key, host, port) in socks5Endpoints)
        {
            try
            {
                var listener = new TcpListener(host, port);
                listener.Start();
                _listeners.Add((listener, key, host, port));
                _logger.Info("SOCKS5 代理服务启动: {Host}:{Port}", host, port);

                // 启动接受连接的任务
                _ = AcceptConnectionsAsync(listener, key, host, port, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SOCKS5 代理服务启动失败: {Host}:{Port}", host, port);
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
        foreach (var (listener, _, _, _) in _listeners)
        {
            listener.Stop();
        }
        
        _logger.Info("SOCKS5 代理服务已停止");
    }

    /// <summary>
    /// 解析端点配置
    /// 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:1080"
    /// </summary>
    private static (string key, IPAddress host, int port)? ParseEndpoint(string key)
    {
        // 尝试解析 host:port 格式
        if (key.Contains(':'))
        {
            var lastColon = key.LastIndexOf(':');
            var hostPart = key[..lastColon];
            var portPart = key[(lastColon + 1)..];
            
            if (int.TryParse(portPart, out var port) && IPAddress.TryParse(hostPart, out var ip))
            {
                return (key, ip, port);
            }
        }
        // 尝试解析纯端口号
        else if (int.TryParse(key, out var port))
        {
            return (key, IPAddress.Any, port);
        }

        return null;
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, string configKey, IPAddress host, int port, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                
                // 异步处理连接
                _ = HandleClientAsync(client, configKey, host, port, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SOCKS5 接受连接失败");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, string configKey, IPAddress host, int port, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                var portConfig = GetPortConfig(_options, host.ToString(), port, configKey);
                var handler = new Socks5Handler(_options, portConfig);
                await handler.HandleAsync(client.Client, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SOCKS5 处理连接失败");
            }
        }
    }

    /// <summary>
    /// 获取端口配置
    /// 优先级: 原始配置键 > host:port > 0.0.0.0:port > port > Default
    /// </summary>
    private static ProxyPortConfig GetPortConfig(ProxyServerOptions options, string localIp, int localPort, string originalKey)
    {
        // 1. 使用原始配置键
        if (options.Ports.TryGetValue(originalKey, out var config))
        {
            return config;
        }

        // 2. 精确匹配 host:port
        var exactKey = $"{localIp}:{localPort}";
        if (options.Ports.TryGetValue(exactKey, out config))
        {
            return config;
        }

        // 3. 匹配 0.0.0.0:port（通配地址）
        var wildcardKey = $"0.0.0.0:{localPort}";
        if (localIp != "0.0.0.0" && options.Ports.TryGetValue(wildcardKey, out config))
        {
            return config;
        }

        // 4. 只匹配端口号
        if (options.Ports.TryGetValue(localPort.ToString(), out config))
        {
            return config;
        }

        // 5. 使用默认配置
        return options.Default;
    }
}
