using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// SOCKS5 代理处理器
/// 实现 RFC 1928 SOCKS5 协议
/// </summary>
public class Socks5Handler
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    
    // SOCKS5 常量
    private const byte SOCKS_VERSION = 0x05;
    private const byte AUTH_NO_AUTH = 0x00;
    private const byte AUTH_USERNAME_PASSWORD = 0x02;
    private const byte AUTH_NO_ACCEPTABLE = 0xFF;
    
    private const byte CMD_CONNECT = 0x01;
    private const byte CMD_BIND = 0x02;
    private const byte CMD_UDP_ASSOCIATE = 0x03;
    
    private const byte ATYP_IPV4 = 0x01;
    private const byte ATYP_DOMAIN = 0x03;
    private const byte ATYP_IPV6 = 0x04;
    
    private const byte REP_SUCCESS = 0x00;
    private const byte REP_GENERAL_FAILURE = 0x01;
    private const byte REP_CONNECTION_NOT_ALLOWED = 0x02;
    private const byte REP_NETWORK_UNREACHABLE = 0x03;
    private const byte REP_HOST_UNREACHABLE = 0x04;
    private const byte REP_CONNECTION_REFUSED = 0x05;
    private const byte REP_TTL_EXPIRED = 0x06;
    private const byte REP_COMMAND_NOT_SUPPORTED = 0x07;
    private const byte REP_ADDRESS_TYPE_NOT_SUPPORTED = 0x08;

    private readonly ProxyServerOptions _options;
    private readonly ProxyPortConfig _portConfig;

    public Socks5Handler(ProxyServerOptions options, ProxyPortConfig portConfig)
    {
        _options = options;
        _portConfig = portConfig;
    }

    /// <summary>
    /// 处理 SOCKS5 连接
    /// </summary>
    public async Task HandleAsync(Socket clientSocket, CancellationToken cancellationToken = default)
    {
        using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);
        
        try
        {
            // 1. 握手阶段 - 认证方法协商
            if (!await HandleGreetingAsync(clientStream, cancellationToken))
            {
                return;
            }

            // 2. 认证阶段（如果需要）
            if (_portConfig.RequireAuth && !string.IsNullOrEmpty(_options.Username))
            {
                if (!await HandleAuthAsync(clientStream, cancellationToken))
                {
                    return;
                }
            }

            // 3. 请求阶段
            await HandleRequestAsync(clientSocket, clientStream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SOCKS5 处理失败");
        }
    }

    /// <summary>
    /// 处理 SOCKS5 握手
    /// </summary>
    private async Task<bool> HandleGreetingAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        
        // 读取版本和认证方法数量
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 2), cancellationToken);
        if (bytesRead < 2 || buffer[0] != SOCKS_VERSION)
        {
            _logger.Warn("无效的 SOCKS5 握手");
            return false;
        }

        var nmethods = buffer[1];
        if (nmethods == 0)
        {
            return false;
        }

        // 读取认证方法列表
        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, nmethods), cancellationToken);
        if (bytesRead < nmethods)
        {
            return false;
        }

        var methods = buffer.Take(nmethods).ToList();
        byte selectedMethod;

        if (_portConfig.RequireAuth && !string.IsNullOrEmpty(_options.Username))
        {
            // 需要认证
            if (methods.Contains(AUTH_USERNAME_PASSWORD))
            {
                selectedMethod = AUTH_USERNAME_PASSWORD;
            }
            else
            {
                selectedMethod = AUTH_NO_ACCEPTABLE;
            }
        }
        else
        {
            // 不需要认证
            if (methods.Contains(AUTH_NO_AUTH))
            {
                selectedMethod = AUTH_NO_AUTH;
            }
            else
            {
                selectedMethod = AUTH_NO_ACCEPTABLE;
            }
        }

        // 发送选择的认证方法
        var response = new byte[] { SOCKS_VERSION, selectedMethod };
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return selectedMethod != AUTH_NO_ACCEPTABLE;
    }

    /// <summary>
    /// 处理用户名/密码认证
    /// RFC 1929
    /// </summary>
    private async Task<bool> HandleAuthAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[513];
        
        // 读取认证版本
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 2), cancellationToken);
        if (bytesRead < 2 || buffer[0] != 0x01) // 认证版本 1
        {
            return false;
        }

        var usernameLen = buffer[1];
        
        // 读取用户名
        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, usernameLen + 1), cancellationToken);
        if (bytesRead < usernameLen + 1)
        {
            return false;
        }

        var username = Encoding.UTF8.GetString(buffer, 0, usernameLen);
        var passwordLen = buffer[usernameLen];

        // 读取密码
        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, passwordLen), cancellationToken);
        if (bytesRead < passwordLen)
        {
            return false;
        }

        var password = Encoding.UTF8.GetString(buffer, 0, passwordLen);

        // 验证
        var success = username == _options.Username && password == _options.Password;

        // 发送认证结果
        var response = new byte[] { 0x01, success ? (byte)0x00 : (byte)0x01 };
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        if (!success)
        {
            _logger.Warn("SOCKS5 认证失败: {Username}", username);
        }

        return success;
    }

    /// <summary>
    /// 处理 SOCKS5 请求
    /// </summary>
    private async Task HandleRequestAsync(Socket clientSocket, NetworkStream clientStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        
        // 读取请求头
        var bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, 4), cancellationToken);
        if (bytesRead < 4)
        {
            return;
        }

        if (buffer[0] != SOCKS_VERSION)
        {
            return;
        }

        var cmd = buffer[1];
        // buffer[2] 是保留字节
        var atyp = buffer[3];

        // 解析目标地址
        string targetHost;
        int targetPort;

        switch (atyp)
        {
            case ATYP_IPV4:
                bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, 6), cancellationToken);
                if (bytesRead < 6) return;
                targetHost = new IPAddress(buffer.Take(4).ToArray()).ToString();
                targetPort = (buffer[4] << 8) | buffer[5];
                break;

            case ATYP_DOMAIN:
                bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                if (bytesRead < 1) return;
                var domainLen = buffer[0];
                bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, domainLen + 2), cancellationToken);
                if (bytesRead < domainLen + 2) return;
                targetHost = Encoding.UTF8.GetString(buffer, 0, domainLen);
                targetPort = (buffer[domainLen] << 8) | buffer[domainLen + 1];
                break;

            case ATYP_IPV6:
                bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, 18), cancellationToken);
                if (bytesRead < 18) return;
                targetHost = new IPAddress(buffer.Take(16).ToArray()).ToString();
                targetPort = (buffer[16] << 8) | buffer[17];
                break;

            default:
                await SendReplyAsync(clientStream, REP_ADDRESS_TYPE_NOT_SUPPORTED, cancellationToken);
                return;
        }

        _logger.Debug("SOCKS5 请求: CMD={Cmd} {Host}:{Port}", cmd, targetHost, targetPort);

        // 检查目标主机是否允许
        if (!IsHostAllowed(targetHost))
        {
            await SendReplyAsync(clientStream, REP_CONNECTION_NOT_ALLOWED, cancellationToken);
            return;
        }

        // 处理命令
        switch (cmd)
        {
            case CMD_CONNECT:
                await HandleConnectAsync(clientSocket, clientStream, targetHost, targetPort, cancellationToken);
                break;

            case CMD_BIND:
            case CMD_UDP_ASSOCIATE:
                await SendReplyAsync(clientStream, REP_COMMAND_NOT_SUPPORTED, cancellationToken);
                break;

            default:
                await SendReplyAsync(clientStream, REP_COMMAND_NOT_SUPPORTED, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// 处理 CONNECT 命令
    /// </summary>
    private async Task HandleConnectAsync(Socket clientSocket, NetworkStream clientStream, 
        string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        Socket? targetSocket = null;
        
        try
        {
            targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            var connectTimeout = TimeSpan.FromSeconds(_options.ConnectTimeout);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(connectTimeout);

            // 使用自定义 DNS 解析（如果配置了）
            var dnsService = ServiceLocator.GetService<Dns.ICustomDnsService>();
            IPAddress? resolvedIp = null;
            if (dnsService != null)
            {
                resolvedIp = await dnsService.ResolveAsync(targetHost, cts.Token);
            }

            if (resolvedIp != null)
            {
                await targetSocket.ConnectAsync(new IPEndPoint(resolvedIp, targetPort), cts.Token);
            }
            else
            {
                await targetSocket.ConnectAsync(targetHost, targetPort, cts.Token);
            }

            // 发送成功响应
            await SendReplyAsync(clientStream, REP_SUCCESS, cancellationToken);

            _logger.Debug("SOCKS5 连接成功: {Host}:{Port}", targetHost, targetPort);

            // 开始双向数据转发
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
            await TunnelAsync(clientStream, targetStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("SOCKS5 连接超时: {Host}:{Port}", targetHost, targetPort);
            await SendReplyAsync(clientStream, REP_TTL_EXPIRED, cancellationToken);
        }
        catch (SocketException ex)
        {
            _logger.Warn("SOCKS5 连接失败: {Host}:{Port} - {Error}", targetHost, targetPort, ex.Message);
            var reply = ex.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable => REP_NETWORK_UNREACHABLE,
                SocketError.HostUnreachable => REP_HOST_UNREACHABLE,
                SocketError.ConnectionRefused => REP_CONNECTION_REFUSED,
                _ => REP_GENERAL_FAILURE
            };
            await SendReplyAsync(clientStream, reply, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SOCKS5 连接异常: {Host}:{Port}", targetHost, targetPort);
            await SendReplyAsync(clientStream, REP_GENERAL_FAILURE, cancellationToken);
        }
        finally
        {
            targetSocket?.Dispose();
        }
    }

    /// <summary>
    /// 发送 SOCKS5 响应
    /// </summary>
    private static async Task SendReplyAsync(NetworkStream stream, byte reply, CancellationToken cancellationToken)
    {
        // VER | REP | RSV | ATYP | BND.ADDR | BND.PORT
        var response = new byte[]
        {
            SOCKS_VERSION,  // VER
            reply,          // REP
            0x00,           // RSV
            ATYP_IPV4,      // ATYP
            0, 0, 0, 0,     // BND.ADDR (0.0.0.0)
            0, 0            // BND.PORT (0)
        };
        
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 双向数据隧道
    /// </summary>
    private async Task TunnelAsync(NetworkStream clientStream, NetworkStream targetStream, CancellationToken cancellationToken)
    {
        var dataTimeout = TimeSpan.FromSeconds(_options.DataTimeout);

        var clientToTarget = CopyStreamAsync(clientStream, targetStream, dataTimeout, cancellationToken);
        var targetToClient = CopyStreamAsync(targetStream, clientStream, dataTimeout, cancellationToken);

        await Task.WhenAny(clientToTarget, targetToClient);
    }

    private static async Task CopyStreamAsync(Stream source, Stream destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                
                var bytesRead = await source.ReadAsync(buffer, cts.Token);
                if (bytesRead == 0) break;
                
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                await destination.FlushAsync(cts.Token);
            }
        }
        catch { }
    }

    /// <summary>
    /// 检查目标主机是否允许
    /// </summary>
    private bool IsHostAllowed(string host)
    {
        // 检查黑名单
        foreach (var blocked in _options.BlockedHosts)
        {
            if (MatchHost(host, blocked))
            {
                return false;
            }
        }

        // 如果有白名单，检查是否在白名单中
        if (_options.AllowedHosts.Count > 0)
        {
            foreach (var allowed in _options.AllowedHosts)
            {
                if (MatchHost(host, allowed))
                {
                    return true;
                }
            }
            return false;
        }

        return true;
    }

    private static bool MatchHost(string host, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                   host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }
        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
