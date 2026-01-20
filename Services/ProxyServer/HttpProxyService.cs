using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// 统一代理服务
/// 作为后台服务运行，监听代理请求
/// 支持 HTTP、HTTPS (CONNECT) 和 SOCKS5 协议
/// 通过嗅探首字节自动判断协议类型
/// </summary>
public class HttpProxyService : BackgroundService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private ProxyServerOptions _options;
    private readonly List<(TcpListener listener, string key, IPAddress host, int port)> _listeners = [];

    // SOCKS5 版本号
    private const byte SOCKS5_VERSION = 0x05;

    public HttpProxyService(IOptionsMonitor<ProxyServerOptions> optionsMonitor)
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
            _logger.Info("代理服务未启用");
            return;
        }

        // 查找启用了任意代理类型的端口配置
        // 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:8080"
        var proxyEndpoints = _options.Ports
            .Where(p => p.Value.EnableHttp || p.Value.EnableHttps || p.Value.EnableSocks5)
            .Select(p => ParseEndpoint(p.Key))
            .Where(e => e.HasValue)
            .Select(e => e!.Value)
            .ToList();

        if (proxyEndpoints.Count == 0)
        {
            _logger.Info("没有配置代理端口");
            return;
        }

        // 启动监听器
        foreach (var (key, host, port) in proxyEndpoints)
        {
            try
            {
                var listener = new TcpListener(host, port);
                listener.Start();
                _listeners.Add((listener, key, host, port));
                
                var portConfig = GetPortConfig(_options, host.ToString(), port, key);
                var protocols = GetEnabledProtocols(portConfig);
                _logger.Info("代理服务启动: {Host}:{Port} ({Protocols})", host, port, protocols);

                // 启动接受连接的任务
                _ = AcceptConnectionsAsync(listener, key, host, port, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "代理服务启动失败: {Host}:{Port}", host, port);
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

        _logger.Info("代理服务已停止");
    }

    /// <summary>
    /// 获取启用的协议名称
    /// </summary>
    private static string GetEnabledProtocols(ProxyPortConfig config)
    {
        var protocols = new List<string>();
        if (config.EnableHttp) protocols.Add("HTTP");
        if (config.EnableHttps) protocols.Add("HTTPS");
        if (config.EnableSocks5) protocols.Add("SOCKS5");
        return string.Join(", ", protocols);
    }

    /// <summary>
    /// 解析端点配置
    /// 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:8080"
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
                _logger.Error(ex, "代理接受连接失败");
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
                var socket = client.Client;

                // 嗅探首字节以判断协议类型
                var peekBuffer = new byte[1];
                var received = await socket.ReceiveAsync(peekBuffer, SocketFlags.Peek, stoppingToken);
                
                if (received == 0)
                {
                    // 连接已关闭
                    return;
                }

                var firstByte = peekBuffer[0];

                // 判断协议类型
                // SOCKS5 以 0x05 开头
                // HTTP 以 ASCII 字母开头 (GET, POST, CONNECT, PUT, DELETE, HEAD, OPTIONS, TRACE, PATCH)
                if (firstByte == SOCKS5_VERSION && portConfig.EnableSocks5)
                {
                    // SOCKS5 协议
                    var handler = new Socks5Handler(_options, portConfig);
                    await handler.HandleAsync(socket, stoppingToken);
                }
                else if (IsHttpMethod(firstByte) && (portConfig.EnableHttp || portConfig.EnableHttps))
                {
                    // HTTP/HTTPS 代理协议
                    var handler = new HttpProxyHandler(_options, portConfig);
                    await handler.HandleAsync(socket, stoppingToken);
                }
                else
                {
                    // 未知协议或协议未启用
                    _logger.Debug("未知或未启用的协议，首字节: 0x{FirstByte:X2}", firstByte);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "代理处理连接失败");
            }
        }
    }

    /// <summary>
    /// 检查首字节是否可能是 HTTP 方法的开头
    /// HTTP 方法: GET, POST, PUT, DELETE, HEAD, OPTIONS, TRACE, PATCH, CONNECT
    /// </summary>
    private static bool IsHttpMethod(byte firstByte)
    {
        // HTTP 方法的首字母: G(ET), P(OST/UT/ATCH), D(ELETE), H(EAD), O(PTIONS), T(RACE), C(ONNECT)
        return firstByte switch
        {
            (byte)'G' => true,  // GET
            (byte)'P' => true,  // POST, PUT, PATCH
            (byte)'D' => true,  // DELETE
            (byte)'H' => true,  // HEAD
            (byte)'O' => true,  // OPTIONS
            (byte)'T' => true,  // TRACE
            (byte)'C' => true,  // CONNECT
            _ => false
        };
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
