using System.Net;
using System.Net.Sockets;
using System.Text;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// 统一代理处理器
/// 同一端口同时支持 HTTP、HTTPS (CONNECT) 和 SOCKS5 三种协议
/// 通过首字节嗅探自动判断协议类型
/// </summary>
public class ProxyHandler
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    // SOCKS5 常量
    private const byte SOCKS_VERSION = 0x05;
    private const byte AUTH_NO_AUTH = 0x00;
    private const byte AUTH_USERNAME_PASSWORD = 0x02;
    private const byte AUTH_NO_ACCEPTABLE = 0xFF;

    private const byte CMD_CONNECT = 0x01;

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

    public ProxyHandler(ProxyServerOptions options, ProxyPortConfig portConfig)
    {
        _options = options;
        _portConfig = portConfig;
    }

    /// <summary>
    /// 根据首字节自动判断协议并处理连接
    /// </summary>
    public async Task HandleAsync(Socket clientSocket, CancellationToken cancellationToken = default)
    {
        using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);

        try
        {
            // 嗅探首字节
            var peekBuffer = new byte[1];
            var received = await clientSocket.ReceiveAsync(peekBuffer, SocketFlags.Peek, cancellationToken);
            if (received == 0) return;

            var firstByte = peekBuffer[0];

            if (firstByte == SOCKS_VERSION && _portConfig.EnableSocks5)
            {
                await HandleSocks5Async(clientSocket, clientStream, cancellationToken);
            }
            else if (IsHttpMethod(firstByte) && (_portConfig.EnableHttp || _portConfig.EnableHttps))
            {
                await HandleHttpAsync(clientStream, cancellationToken);
            }
            else
            {
                _logger.Debug("未知或未启用的协议，首字节: 0x{FirstByte:X2}", firstByte);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理处理连接失败");
        }
    }

    // ══════════════════════════════════════════
    //  HTTP / HTTPS 协议处理
    // ══════════════════════════════════════════

    private async Task HandleHttpAsync(NetworkStream clientStream, CancellationToken cancellationToken)
    {
        var requestLine = await ReadLineAsync(clientStream, cancellationToken);
        if (string.IsNullOrEmpty(requestLine)) return;

        var parts = requestLine.Split(' ');
        if (parts.Length < 3)
        {
            await SendHttpErrorAsync(clientStream, 400, "Bad Request", cancellationToken);
            return;
        }

        var method = parts[0].ToUpper();
        var target = parts[1];
        var httpVersion = parts[2];

        var headers = await ReadHeadersAsync(clientStream, cancellationToken);

        // 认证检查
        if (_portConfig.RequireAuth && !string.IsNullOrEmpty(_options.Username))
        {
            if (!CheckHttpProxyAuth(headers))
            {
                await SendProxyAuthRequiredAsync(clientStream, cancellationToken);
                return;
            }
        }

        if (method == "CONNECT" && _portConfig.EnableHttps)
        {
            await HandleHttpConnectAsync(clientStream, target, cancellationToken);
        }
        else if (_portConfig.EnableHttp && (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                             target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            await HandleHttpProxyAsync(clientStream, method, target, httpVersion, headers, cancellationToken);
        }
        else
        {
            await SendHttpErrorAsync(clientStream, 400, "Bad Request", cancellationToken);
        }
    }

    /// <summary>
    /// HTTPS CONNECT 隧道
    /// </summary>
    private async Task HandleHttpConnectAsync(NetworkStream clientStream, string target, CancellationToken cancellationToken)
    {
        var (targetHost, targetPort) = ParseHostPort(target, 443);
        _logger.Debug("代理 CONNECT 请求: {Host}:{Port}", targetHost, targetPort);

        if (!IsHostAllowed(targetHost))
        {
            await SendHttpErrorAsync(clientStream, 403, "Forbidden", cancellationToken);
            return;
        }

        Socket? targetSocket = null;
        try
        {
            targetSocket = await ConnectTargetAsync(targetHost, targetPort, cancellationToken);

            var response = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
            await clientStream.WriteAsync(response, cancellationToken);
            await clientStream.FlushAsync(cancellationToken);

            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
            await TunnelAsync(clientStream, targetStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("代理连接超时: {Host}:{Port}", targetHost, targetPort);
            await SendHttpErrorAsync(clientStream, 504, "Gateway Timeout", cancellationToken);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            _logger.Warn("代理目标主机未找到: {Host}:{Port}", targetHost, targetPort);
            await SendHttpErrorAsync(clientStream, 502, "Bad Gateway: Host not found", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理 CONNECT 失败: {Host}:{Port}", targetHost, targetPort);
            await SendHttpErrorAsync(clientStream, 502, "Bad Gateway", cancellationToken);
        }
        finally
        {
            targetSocket?.Dispose();
        }
    }

    /// <summary>
    /// 普通 HTTP 代理
    /// </summary>
    private async Task HandleHttpProxyAsync(NetworkStream clientStream, string method, string targetUrl,
        string httpVersion, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        _logger.Debug("代理 HTTP 请求: {Method} {Url}", method, targetUrl);

        try
        {
            var uri = new Uri(targetUrl);
            var targetHost = uri.Host;
            var targetPort = uri.Port;

            if (!IsHostAllowed(targetHost))
            {
                await SendHttpErrorAsync(clientStream, 403, "Forbidden", cancellationToken);
                return;
            }

            using var targetSocket = await ConnectTargetAsync(targetHost, targetPort, cancellationToken);
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);

            // 构建转发请求
            var requestPath = uri.PathAndQuery;
            if (string.IsNullOrEmpty(requestPath)) requestPath = "/";

            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(requestPath).Append(' ').Append(httpVersion).Append("\r\n");

            foreach (var header in headers)
            {
                if (header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            if (!headers.ContainsKey("Host"))
                sb.Append("Host: ").Append(uri.Host).Append("\r\n");

            sb.Append("\r\n");

            await targetStream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), cancellationToken);
            await targetStream.FlushAsync(cancellationToken);

            // 转发请求体
            if (headers.TryGetValue("Content-Length", out var clStr) &&
                int.TryParse(clStr, out var contentLength) && contentLength > 0)
            {
                await CopyBytesAsync(clientStream, targetStream, contentLength, cancellationToken);
            }

            // 转发响应
            await CopyResponseAsync(targetStream, clientStream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理 HTTP 失败: {Method} {Url}", method, targetUrl);
            await SendHttpErrorAsync(clientStream, 502, "Bad Gateway", cancellationToken);
        }
    }

    // ══════════════════════════════════════════
    //  SOCKS5 协议处理
    // ══════════════════════════════════════════

    private async Task HandleSocks5Async(Socket clientSocket, NetworkStream clientStream, CancellationToken cancellationToken)
    {
        try
        {
            // 1. 握手 - 认证方法协商
            if (!await Socks5GreetingAsync(clientStream, cancellationToken))
                return;

            // 2. 认证（如果需要）
            if (_portConfig.RequireAuth && !string.IsNullOrEmpty(_options.Username))
            {
                if (!await Socks5AuthAsync(clientStream, cancellationToken))
                    return;
            }

            // 3. 请求处理
            await Socks5RequestAsync(clientSocket, clientStream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SOCKS5 处理失败");
        }
    }

    private async Task<bool> Socks5GreetingAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];

        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 2), cancellationToken);
        if (bytesRead < 2 || buffer[0] != SOCKS_VERSION)
        {
            _logger.Warn("无效的 SOCKS5 握手");
            return false;
        }

        var nmethods = buffer[1];
        if (nmethods == 0) return false;

        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, nmethods), cancellationToken);
        if (bytesRead < nmethods) return false;

        var methods = buffer.Take(nmethods).ToList();
        byte selectedMethod;

        if (_portConfig.RequireAuth && !string.IsNullOrEmpty(_options.Username))
        {
            selectedMethod = methods.Contains(AUTH_USERNAME_PASSWORD) ? AUTH_USERNAME_PASSWORD : AUTH_NO_ACCEPTABLE;
        }
        else
        {
            selectedMethod = methods.Contains(AUTH_NO_AUTH) ? AUTH_NO_AUTH : AUTH_NO_ACCEPTABLE;
        }

        await stream.WriteAsync(new byte[] { SOCKS_VERSION, selectedMethod }, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return selectedMethod != AUTH_NO_ACCEPTABLE;
    }

    private async Task<bool> Socks5AuthAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[513];

        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 2), cancellationToken);
        if (bytesRead < 2 || buffer[0] != 0x01) return false;

        var usernameLen = buffer[1];
        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, usernameLen + 1), cancellationToken);
        if (bytesRead < usernameLen + 1) return false;

        var username = Encoding.UTF8.GetString(buffer, 0, usernameLen);
        var passwordLen = buffer[usernameLen];

        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, passwordLen), cancellationToken);
        if (bytesRead < passwordLen) return false;

        var password = Encoding.UTF8.GetString(buffer, 0, passwordLen);

        var success = username == _options.Username && password == _options.Password;

        await stream.WriteAsync(new byte[] { 0x01, success ? (byte)0x00 : (byte)0x01 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        if (!success) _logger.Warn("SOCKS5 认证失败: {Username}", username);
        return success;
    }

    private async Task Socks5RequestAsync(Socket clientSocket, NetworkStream clientStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];

        var bytesRead = await clientStream.ReadAsync(buffer.AsMemory(0, 4), cancellationToken);
        if (bytesRead < 4 || buffer[0] != SOCKS_VERSION) return;

        var cmd = buffer[1];
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
                await Socks5ReplyAsync(clientStream, REP_ADDRESS_TYPE_NOT_SUPPORTED, cancellationToken);
                return;
        }

        _logger.Debug("SOCKS5 请求: CMD={Cmd} {Host}:{Port}", cmd, targetHost, targetPort);

        if (!IsHostAllowed(targetHost))
        {
            await Socks5ReplyAsync(clientStream, REP_CONNECTION_NOT_ALLOWED, cancellationToken);
            return;
        }

        if (cmd != CMD_CONNECT)
        {
            await Socks5ReplyAsync(clientStream, REP_COMMAND_NOT_SUPPORTED, cancellationToken);
            return;
        }

        await Socks5ConnectAsync(clientSocket, clientStream, targetHost, targetPort, cancellationToken);
    }

    private async Task Socks5ConnectAsync(Socket clientSocket, NetworkStream clientStream,
        string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        Socket? targetSocket = null;
        try
        {
            targetSocket = await ConnectTargetAsync(targetHost, targetPort, cancellationToken);

            await Socks5ReplyAsync(clientStream, REP_SUCCESS, cancellationToken);
            _logger.Debug("SOCKS5 连接成功: {Host}:{Port}", targetHost, targetPort);

            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
            await TunnelAsync(clientStream, targetStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("SOCKS5 连接超时: {Host}:{Port}", targetHost, targetPort);
            await Socks5ReplyAsync(clientStream, REP_TTL_EXPIRED, cancellationToken);
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
            await Socks5ReplyAsync(clientStream, reply, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SOCKS5 连接异常: {Host}:{Port}", targetHost, targetPort);
            await Socks5ReplyAsync(clientStream, REP_GENERAL_FAILURE, cancellationToken);
        }
        finally
        {
            targetSocket?.Dispose();
        }
    }

    private static async Task Socks5ReplyAsync(NetworkStream stream, byte reply, CancellationToken cancellationToken)
    {
        var response = new byte[]
        {
            SOCKS_VERSION, reply, 0x00, ATYP_IPV4,
            0, 0, 0, 0,  // BND.ADDR
            0, 0          // BND.PORT
        };
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    // ══════════════════════════════════════════
    //  公共方法（DNS 解析、隧道、主机匹配等）
    // ══════════════════════════════════════════

    /// <summary>
    /// 连接到目标服务器（含自定义 DNS 解析）
    /// </summary>
    private async Task<Socket> ConnectTargetAsync(string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var connectTimeout = TimeSpan.FromSeconds(_options.ConnectTimeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(connectTimeout);

        // 自定义 DNS 解析
        var dnsService = ServiceLocator.GetService<Dns.ICustomDnsService>();
        IPAddress? resolvedIp = null;
        if (dnsService != null)
        {
            resolvedIp = await dnsService.ResolveAsync(targetHost, cts.Token);
        }

        IPAddress targetIp;
        if (resolvedIp != null)
        {
            targetIp = resolvedIp;
        }
        else
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(targetHost, cts.Token);
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);
            targetIp = addresses[0];
        }

        var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(targetIp, targetPort), cts.Token);
            _logger.Debug("已连接到目标: {Host}:{Port} -> {IP}", targetHost, targetPort, targetIp);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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
        foreach (var blocked in _options.BlockedHosts)
        {
            if (MatchHost(host, blocked)) return false;
        }

        if (_options.AllowedHosts.Count > 0)
        {
            foreach (var allowed in _options.AllowedHosts)
            {
                if (MatchHost(host, allowed)) return true;
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

    // ══════════════════════════════════════════
    //  HTTP 协议辅助方法
    // ══════════════════════════════════════════

    private static bool IsHttpMethod(byte firstByte)
    {
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

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var singleByte = new byte[1];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(singleByte, cancellationToken);
            if (read == 0) break;

            if (singleByte[0] == '\n')
            {
                if (buffer.Count > 0 && buffer[^1] == '\r')
                    buffer.RemoveAt(buffer.Count - 1);
                break;
            }

            buffer.Add(singleByte[0]);
        }

        return buffer.Count > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
    }

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(stream, cancellationToken);
            if (string.IsNullOrEmpty(line)) break;

            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();
                headers[key] = value;
            }
        }

        return headers;
    }

    private bool CheckHttpProxyAuth(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Proxy-Authorization", out var authHeader))
            return false;

        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader[6..]));
            var parts = credentials.Split(':', 2);
            return parts.Length == 2 && parts[0] == _options.Username && parts[1] == _options.Password;
        }
        catch { return false; }
    }

    private static async Task SendProxyAuthRequiredAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Proxy\"\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task SendHttpErrorAsync(NetworkStream stream, int statusCode, string message, CancellationToken cancellationToken)
    {
        try
        {
            var body = $"<html><body><h1>{statusCode} {message}</h1></body></html>";
            var response = $"HTTP/1.1 {statusCode} {message}\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch { }
    }

    private static (string host, int port) ParseHostPort(string target, int defaultPort)
    {
        var colonIndex = target.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(target[(colonIndex + 1)..], out var port))
            return (target[..colonIndex], port);
        return (target, defaultPort);
    }

    private static async Task CopyBytesAsync(Stream source, Stream destination, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var remaining = count;

        while (remaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task CopyResponseAsync(NetworkStream source, NetworkStream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        catch { }
    }
}
