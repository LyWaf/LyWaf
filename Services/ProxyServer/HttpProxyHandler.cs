using System.Net;
using System.Net.Sockets;
using System.Text;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// HTTP/HTTPS 代理处理器
/// 处理 HTTP CONNECT 方法（HTTPS 隧道）和普通 HTTP 代理请求
/// </summary>
public class HttpProxyHandler
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly ProxyServerOptions _options;
    private readonly ProxyPortConfig _portConfig;

    public HttpProxyHandler(ProxyServerOptions options, ProxyPortConfig portConfig)
    {
        _options = options;
        _portConfig = portConfig;
    }

    /// <summary>
    /// 处理 HTTP 代理连接
    /// </summary>
    public async Task HandleAsync(Socket clientSocket, CancellationToken cancellationToken = default)
    {
        using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);

        try
        {
            // 读取 HTTP 请求行
            var requestLine = await ReadLineAsync(clientStream, cancellationToken);
            if (string.IsNullOrEmpty(requestLine))
            {
                return;
            }

            var parts = requestLine.Split(' ');
            if (parts.Length < 3)
            {
                await SendErrorResponseAsync(clientStream, 400, "Bad Request", cancellationToken);
                return;
            }

            var method = parts[0].ToUpper();
            var target = parts[1];
            var httpVersion = parts[2];

            // 读取所有请求头
            var headers = await ReadHeadersAsync(clientStream, cancellationToken);

            // 检查认证
            if (_portConfig.RequireAuth && !string.IsNullOrEmpty(_options.Username))
            {
                if (!CheckProxyAuth(headers))
                {
                    await SendProxyAuthRequiredAsync(clientStream, cancellationToken);
                    return;
                }
            }

            if (method == "CONNECT" && _portConfig.EnableHttps)
            {
                // HTTPS 代理 (CONNECT 隧道)
                await HandleConnectAsync(clientSocket, clientStream, target, cancellationToken);
            }
            else if (_portConfig.EnableHttp && (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                                 target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                // HTTP 代理请求
                await HandleHttpProxyAsync(clientStream, method, target, httpVersion, headers, cancellationToken);
            }
            else
            {
                await SendErrorResponseAsync(clientStream, 400, "Bad Request", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "HTTP 代理处理失败");
        }
    }

    /// <summary>
    /// 处理 CONNECT 方法（HTTPS 隧道）
    /// </summary>
    private async Task HandleConnectAsync(Socket clientSocket, NetworkStream clientStream, 
        string target, CancellationToken cancellationToken)
    {
        // 解析目标地址
        var (targetHost, targetPort) = ParseHostPort(target, 443);

        _logger.Debug("代理 CONNECT 请求: {Host}:{Port}", targetHost, targetPort);

        // 检查目标主机是否允许
        if (!IsHostAllowed(targetHost))
        {
            await SendErrorResponseAsync(clientStream, 403, "Forbidden", cancellationToken);
            return;
        }

        Socket? targetSocket = null;
        try
        {
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

            // 如果没有自定义 DNS 解析结果，使用系统 DNS
            IPAddress targetIp;
            if (resolvedIp != null)
            {
                targetIp = resolvedIp;
            }
            else
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(targetHost, cts.Token);
                if (addresses.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }
                targetIp = addresses[0];
            }

            // 根据目标 IP 地址类型创建 Socket
            var addressFamily = targetIp.AddressFamily;
            targetSocket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            // 连接目标服务器
            await targetSocket.ConnectAsync(new IPEndPoint(targetIp, targetPort), cts.Token);

            _logger.Debug("已连接到目标服务器: {Host}:{Port} -> {IP}", targetHost, targetPort, targetIp);

            // 发送 200 Connection Established 响应
            var response = "HTTP/1.1 200 Connection Established\r\n\r\n";
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
            await clientStream.FlushAsync(cancellationToken);

            // 开始双向数据转发
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
            await TunnelAsync(clientStream, targetStream, cancellationToken);

            _logger.Debug("代理 CONNECT 完成: {Host}:{Port}", targetHost, targetPort);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("代理连接超时: {Host}:{Port}", targetHost, targetPort);
            await SendErrorResponseAsync(clientStream, 504, "Gateway Timeout", cancellationToken);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            _logger.Warn("代理目标主机未找到: {Host}:{Port}", targetHost, targetPort);
            await SendErrorResponseAsync(clientStream, 502, "Bad Gateway: Host not found", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理 CONNECT 失败: {Host}:{Port}", targetHost, targetPort);
            await SendErrorResponseAsync(clientStream, 502, "Bad Gateway", cancellationToken);
        }
        finally
        {
            targetSocket?.Dispose();
        }
    }

    /// <summary>
    /// 处理普通 HTTP 代理请求
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

            // 检查目标主机是否允许
            if (!IsHostAllowed(targetHost))
            {
                await SendErrorResponseAsync(clientStream, 403, "Forbidden", cancellationToken);
                return;
            }

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

            // 如果没有自定义 DNS 解析结果，使用系统 DNS
            IPAddress targetIp;
            if (resolvedIp != null)
            {
                targetIp = resolvedIp;
            }
            else
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(targetHost, cts.Token);
                if (addresses.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }
                targetIp = addresses[0];
            }

            // 连接目标服务器
            using var targetSocket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            await targetSocket.ConnectAsync(new IPEndPoint(targetIp, targetPort), cts.Token);

            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);

            // 构建发送到目标服务器的请求
            var requestPath = uri.PathAndQuery;
            if (string.IsNullOrEmpty(requestPath)) requestPath = "/";

            var requestBuilder = new StringBuilder();
            requestBuilder.AppendLine($"{method} {requestPath} {httpVersion}");

            // 复制请求头（排除代理相关头）
            foreach (var header in headers)
            {
                if (header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                requestBuilder.AppendLine($"{header.Key}: {header.Value}");
            }

            // 确保有 Host 头
            if (!headers.ContainsKey("Host"))
            {
                requestBuilder.AppendLine($"Host: {uri.Host}");
            }

            requestBuilder.AppendLine(); // 空行表示头结束

            // 发送请求头到目标服务器
            var requestBytes = Encoding.UTF8.GetBytes(requestBuilder.ToString());
            await targetStream.WriteAsync(requestBytes, cancellationToken);
            await targetStream.FlushAsync(cancellationToken);

            // 如果有请求体，转发请求体
            if (headers.TryGetValue("Content-Length", out var contentLengthStr) &&
                int.TryParse(contentLengthStr, out var contentLength) && contentLength > 0)
            {
                await CopyBytesAsync(clientStream, targetStream, contentLength, cancellationToken);
            }

            // 读取目标服务器响应并转发给客户端
            await CopyResponseAsync(targetStream, clientStream, cancellationToken);

            _logger.Debug("代理 HTTP 完成: {Method} {Url}", method, targetUrl);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理 HTTP 失败: {Method} {Url}", method, targetUrl);
            await SendErrorResponseAsync(clientStream, 502, "Bad Gateway", cancellationToken);
        }
    }

    /// <summary>
    /// 读取一行
    /// </summary>
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
                // 移除可能的 \r
                if (buffer.Count > 0 && buffer[^1] == '\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }
                break;
            }

            buffer.Add(singleByte[0]);
        }

        return buffer.Count > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
    }

    /// <summary>
    /// 读取所有请求头
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(stream, cancellationToken);
            if (string.IsNullOrEmpty(line))
            {
                break; // 空行表示头结束
            }

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

    /// <summary>
    /// 检查代理认证
    /// </summary>
    private bool CheckProxyAuth(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Proxy-Authorization", out var authHeader))
        {
            return false;
        }

        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var encodedCredentials = authHeader[6..];
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
            var parts = credentials.Split(':', 2);
            if (parts.Length == 2)
            {
                return parts[0] == _options.Username && parts[1] == _options.Password;
            }
        }
        catch
        {
            // 解析失败
        }

        return false;
    }

    /// <summary>
    /// 发送代理认证要求响应
    /// </summary>
    private static async Task SendProxyAuthRequiredAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response = "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                      "Proxy-Authenticate: Basic realm=\"Proxy\"\r\n" +
                      "Content-Length: 0\r\n" +
                      "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 发送错误响应
    /// </summary>
    private static async Task SendErrorResponseAsync(NetworkStream stream, int statusCode, string message, CancellationToken cancellationToken)
    {
        try
        {
            var body = $"<html><body><h1>{statusCode} {message}</h1></body></html>";
            var response = $"HTTP/1.1 {statusCode} {message}\r\n" +
                          $"Content-Type: text/html\r\n" +
                          $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                          $"Connection: close\r\n" +
                          $"\r\n" +
                          body;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch
        {
            // 忽略发送错误响应时的异常
        }
    }

    /// <summary>
    /// 解析主机和端口
    /// </summary>
    private static (string host, int port) ParseHostPort(string target, int defaultPort)
    {
        var colonIndex = target.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(target[(colonIndex + 1)..], out var port))
        {
            return (target[..colonIndex], port);
        }
        return (target, defaultPort);
    }

    /// <summary>
    /// 复制指定字节数
    /// </summary>
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

    /// <summary>
    /// 复制 HTTP 响应
    /// </summary>
    private static async Task CopyResponseAsync(NetworkStream source, NetworkStream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        // 简单地复制所有数据直到连接关闭
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
        catch
        {
            // 连接关闭
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
