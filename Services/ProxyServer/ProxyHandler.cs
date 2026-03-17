using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// 统一代理处理器
/// 同一端口同时支持 HTTP、HTTPS (CONNECT) 和 SOCKS5 三种协议
/// 通过首字节嗅探自动判断协议类型
/// 支持 Pcap 模式解密 HTTPS 流量
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

    /// <summary>
    /// CRL 文件在代理端口上的访问路径
    /// </summary>
    internal const string CrlPath = "/lywaf-proxy-ca.crl";

    /// <summary>
    /// CA 证书在代理端口上的下载路径
    /// </summary>
    internal const string CaCertPath = "/lywaf-proxy-ca.crt";

    /// <summary>
    /// 记录 Pcap TLS 握手失败的 clientIp:host，在过期前直接走隧道模式
    /// Value 为失败时间，超过过期时间后自动重试 Pcap
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _pcapFallbackCache = new();
    private static readonly TimeSpan PcapFallbackExpiry = TimeSpan.FromMinutes(2);

    private readonly ProxyServerOptions _options;
    private readonly ProxyPortConfig _portConfig;
    private readonly PcapCertProvider? _pcapCertProvider;

    public ProxyHandler(ProxyServerOptions options, ProxyPortConfig portConfig, PcapCertProvider? pcapCertProvider = null)
    {
        _options = options;
        _portConfig = portConfig;
        _pcapCertProvider = pcapCertProvider;
    }

    /// <summary>
    /// 根据首字节自动判断协议并处理连接
    /// </summary>
    public async Task HandleAsync(Socket clientSocket, CancellationToken cancellationToken = default)
    {
        using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);
        var clientIp = (clientSocket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

        try
        {
            // 嗅探首字节
            var peekBuffer = new byte[1];
            var received = await clientSocket.ReceiveAsync(peekBuffer, SocketFlags.Peek, cancellationToken);
            if (received == 0) return;

            var firstByte = peekBuffer[0];

            if (firstByte == SOCKS_VERSION && _portConfig.EnableSocks5)
            {
                await HandleSocks5Async(clientSocket, clientStream, clientIp, cancellationToken);
            }
            else if (IsHttpMethod(firstByte) && (_portConfig.EnableHttp || _portConfig.EnableHttps))
            {
                await HandleHttpAsync(clientStream, clientIp, cancellationToken);
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

    private async Task HandleHttpAsync(NetworkStream clientStream, string clientIp, CancellationToken cancellationToken)
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

        // CRL 分发点 / CA 证书下载请求（直接 HTTP 请求，非代理格式）
        if (method == "GET" && _portConfig.EnablePcap && _pcapCertProvider != null
            && (target.Equals(CrlPath, StringComparison.OrdinalIgnoreCase)
                || target.Equals(CaCertPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (target.Equals(CrlPath, StringComparison.OrdinalIgnoreCase))
                await ServeCrlAsync(clientStream, cancellationToken);
            else
                await ServeCaCertAsync(clientStream, cancellationToken);
        }
        else if (method == "CONNECT" && _portConfig.EnableHttps)
        {
            await HandleHttpConnectAsync(clientStream, target, clientIp, cancellationToken);
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
    /// HTTPS CONNECT 处理（隧道模式或 Pcap 抓包模式）
    /// </summary>
    private async Task HandleHttpConnectAsync(NetworkStream clientStream, string target, string clientIp, CancellationToken cancellationToken)
    {
        var (targetHost, targetPort) = ParseHostPort(target, 443);
        _logger.Debug("代理 CONNECT 请求: {Host}:{Port}", targetHost, targetPort);

        if (!IsHostAllowed(targetHost))
        {
            await SendHttpErrorAsync(clientStream, 403, "Forbidden", cancellationToken);
            return;
        }

        if (_portConfig.EnablePcap && _pcapCertProvider != null
            && !IsPcapFallback(clientIp, targetHost))
        {
            await HandlePcapConnectAsync(clientStream, targetHost, targetPort, clientIp, cancellationToken);
        }
        else
        {
            await HandleTunnelConnectAsync(clientStream, targetHost, targetPort, cancellationToken);
        }
    }

    /// <summary>
    /// HTTPS CONNECT 隧道模式（透传，不解密）
    /// </summary>
    private async Task HandleTunnelConnectAsync(NetworkStream clientStream, string targetHost, int targetPort, CancellationToken cancellationToken)
    {
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
    /// Pcap HTTPS 抓包模式
    /// 1. 连接目标建立真实 TLS
    /// 2. 回复客户端 200
    /// 3. 用伪造证书与客户端建立 TLS
    /// 4. 在两个 SslStream 之间中继 HTTP 请求/响应并记录
    /// </summary>
    /// <summary>
    /// 检查 clientIp:host 是否在 Pcap 降级缓存中（未过期）
    /// </summary>
    private static bool IsPcapFallback(string clientIp, string host)
    {
        var key = $"{clientIp}:{host}";
        if (_pcapFallbackCache.TryGetValue(key, out var failTime))
        {
            if (DateTime.UtcNow - failTime < PcapFallbackExpiry)
                return true;
            // 已过期，移除
            _pcapFallbackCache.TryRemove(key, out _);
        }
        return false;
    }

    private async Task HandlePcapConnectAsync(NetworkStream clientStream, string targetHost, int targetPort, string clientIp, CancellationToken cancellationToken)
    {
        Socket? targetSocket = null;
        try
        {
            // 1. 先建立 TCP 连接（不做 TLS）
            targetSocket = await ConnectTargetAsync(targetHost, targetPort, cancellationToken);
            using var targetNetStream = new NetworkStream(targetSocket, ownsSocket: false);

            // 2. 先告知客户端隧道已建立
            var established = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
            await clientStream.WriteAsync(established, cancellationToken);
            await clientStream.FlushAsync(cancellationToken);

            // 3. 尝试用伪造证书与客户端建立 TLS（这步可能失败）
            SslStream? clientSsl = null;
            try
            {
                var fakeCert = _pcapCertProvider!.GetCertForHost(targetHost);
                clientSsl = new SslStream(clientStream, leaveInnerStreamOpen: true);
                await clientSsl.AuthenticateAsServerAsync(fakeCert, false, false);
            }
            catch (AuthenticationException ex)
            {
                // 假证书握手失败 → 降级为隧道透传
                // 客户端的 TLS ClientHello 已被消费，无法恢复，关闭连接
                var inner = ex.InnerException?.Message ?? "无详细信息";
                _logger.Warn("Pcap 假证书握手失败，{Min}分钟内降级为隧道模式: {ClientIp} → {Host}:{Port} ({Inner})",
                    PcapFallbackExpiry.TotalMinutes, clientIp, targetHost, targetPort, inner);
                _pcapFallbackCache[$"{clientIp}:{targetHost}"] = DateTime.UtcNow;
                clientSsl?.Dispose();
                return;
            }

            // 4. 与目标建立真实 TLS 连接
            var targetSsl = new SslStream(targetNetStream, leaveInnerStreamOpen: true);
            try
            {
                await targetSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                }, cancellationToken);
            }
            catch (AuthenticationException ex)
            {
                var inner = ex.InnerException?.Message ?? "无详细信息";
                _logger.Warn("Pcap 目标 TLS 握手失败: {Host}:{Port} ({Inner})", targetHost, targetPort, inner);
                targetSsl.Dispose();
                clientSsl.Dispose();
                return;
            }

            _logger.Debug("Pcap 已建立: {Host}:{Port}", targetHost, targetPort);

            // 5. 在两个 SslStream 之间中继 HTTP 流量
            await PcapRelayAsync(clientSsl, targetSsl, targetHost, clientIp, cancellationToken);

            await clientSsl.ShutdownAsync();
            await targetSsl.ShutdownAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Pcap 连接超时: {Host}:{Port}", targetHost, targetPort);
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            _logger.Debug("Pcap 连接中断: {Host}:{Port} - {Message}", targetHost, targetPort, ex.InnerException.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pcap 处理失败: {Host}:{Port}", targetHost, targetPort);
        }
        finally
        {
            targetSocket?.Dispose();
        }
    }

    /// <summary>
    /// Pcap 中继：从客户端 SslStream 读取 HTTP 请求，转发到目标 SslStream，记录流量
    /// 支持 keep-alive 多次请求
    /// </summary>
    private async Task PcapRelayAsync(SslStream clientSsl, SslStream targetSsl, string targetHost, string clientIp, CancellationToken cancellationToken)
    {
        var dataTimeout = TimeSpan.FromSeconds(_options.DataTimeout);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(dataTimeout);

            // 读取客户端请求行
            var requestLine = await ReadLineFromSslAsync(clientSsl, timeoutCts.Token);
            if (string.IsNullOrEmpty(requestLine)) break;

            var parts = requestLine.Split(' ');
            if (parts.Length < 3) break;

            var method = parts[0];
            var path = parts[1];
            var httpVersion = parts[2];

            // 读取请求头
            var headers = await ReadHeadersFromSslAsync(clientSsl, timeoutCts.Token);

            _logger.Info("Pcap {Method} https://{Host}{Path}", method, targetHost, path);

            // 构建转发请求
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(' ').Append(httpVersion).Append("\r\n");
            foreach (var header in headers)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            sb.Append("\r\n");

            // 发送请求头到目标
            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            await targetSsl.WriteAsync(headerBytes, timeoutCts.Token);
            await targetSsl.FlushAsync(timeoutCts.Token);

            // 转发请求体（带捕获）
            string? requestBody = null;
            if (headers.TryGetValue("Content-Length", out var clStr) &&
                int.TryParse(clStr, out var contentLength) && contentLength > 0)
            {
                requestBody = await CopyBytesWithCaptureAsync(clientSsl, targetSsl, contentLength, timeoutCts.Token);
            }

            // 读取响应行
            var responseLine = await ReadLineFromSslAsync(targetSsl, timeoutCts.Token);
            if (string.IsNullOrEmpty(responseLine)) break;

            // 解析状态码
            var statusCode = 0;
            var respParts = responseLine.Split(' ', 3);
            if (respParts.Length >= 2) int.TryParse(respParts[1], out statusCode);

            // 读取响应头
            var responseHeaders = await ReadHeadersFromSslAsync(targetSsl, timeoutCts.Token);

            // 发送响应头到客户端
            var respSb = new StringBuilder();
            respSb.Append(responseLine).Append("\r\n");
            foreach (var header in responseHeaders)
            {
                respSb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            respSb.Append("\r\n");

            var respHeaderBytes = Encoding.UTF8.GetBytes(respSb.ToString());
            await clientSsl.WriteAsync(respHeaderBytes, timeoutCts.Token);
            await clientSsl.FlushAsync(timeoutCts.Token);

            // 转发响应体（带捕获）
            string? responseBody = null;
            if (responseHeaders.TryGetValue("Content-Length", out var respClStr) &&
                int.TryParse(respClStr, out var respContentLength) && respContentLength > 0)
            {
                responseBody = await CopyBytesWithCaptureAsync(targetSsl, clientSsl, respContentLength, timeoutCts.Token);
            }
            else if (responseHeaders.TryGetValue("Transfer-Encoding", out var te) &&
                     te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                await CopyChunkedAsync(targetSsl, clientSsl, timeoutCts.Token);
            }

            await clientSsl.FlushAsync(timeoutCts.Token);

            // 写入抓包日志
            _ = Task.Run(() => WritePcapLog(method, path, targetHost, clientIp, headers, requestBody, statusCode, responseHeaders, responseBody), CancellationToken.None);

            // 检查 Connection: close
            if (headers.TryGetValue("Connection", out var conn) &&
                conn.Equals("close", StringComparison.OrdinalIgnoreCase))
                break;
            if (responseHeaders.TryGetValue("Connection", out var respConn) &&
                respConn.Equals("close", StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    /// <summary>
    /// 转发字节并捕获内容（最多 64KB）
    /// </summary>
    private static async Task<string?> CopyBytesWithCaptureAsync(Stream source, Stream destination, int count, CancellationToken cancellationToken, int maxCapture = 65536)
    {
        var buffer = new byte[8192];
        var remaining = count;
        var captureBuffer = new MemoryStream(Math.Min(count, maxCapture));

        while (remaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (captureBuffer.Length < maxCapture)
            {
                var toCapture = (int)Math.Min(read, maxCapture - captureBuffer.Length);
                captureBuffer.Write(buffer, 0, toCapture);
            }

            remaining -= read;
        }

        await destination.FlushAsync(cancellationToken);

        if (captureBuffer.Length == 0) return null;
        return Encoding.UTF8.GetString(captureBuffer.GetBuffer(), 0, (int)captureBuffer.Length);
    }

    private static readonly SemaphoreSlim _pcapLogLock = new(1, 1);

    /// <summary>
    /// 写入抓包日志（与拦截明细日志格式一致，供 LogUtil.ParseLogFileAsync 解析）
    /// </summary>
    private static void WritePcapLog(string method, string path, string host, string clientIp,
        Dictionary<string, string> headers, string? requestBody,
        int statusCode, Dictionary<string, string> responseHeaders, string? responseBody)
    {
        try
        {
            var logSb = new StringBuilder();
            logSb.AppendLine($"========== [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ==========");
            logSb.AppendLine($"{method} {path} HTTP/1.1");
            logSb.AppendLine($"Host: {host}");
            logSb.AppendLine($"Client-IP: {clientIp}");
            logSb.AppendLine($"Scheme: https");
            logSb.AppendLine($"Status: {statusCode}");

            // 请求头
            logSb.AppendLine("--- Headers ---");
            foreach (var h in headers)
            {
                logSb.AppendLine($"{h.Key}: {h.Value}");
            }

            // 请求体
            if (!string.IsNullOrEmpty(requestBody))
            {
                logSb.AppendLine("--- Request Body ---");
                logSb.AppendLine(requestBody);
            }

            // 响应体
            if (!string.IsNullOrEmpty(responseBody))
            {
                logSb.AppendLine("--- Response Body ---");
                logSb.AppendLine(responseBody);
            }

            logSb.AppendLine();

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            Directory.CreateDirectory(dir);
            var fileName = $"pcap_{DateTime.Now:yyyy-MM-dd}.log";
            var filePath = Path.Combine(dir, fileName);

            _pcapLogLock.Wait();
            try
            {
                File.AppendAllText(filePath, logSb.ToString(), Encoding.UTF8);
            }
            finally
            {
                _pcapLogLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入抓包日志失败");
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

    private async Task HandleSocks5Async(Socket clientSocket, NetworkStream clientStream, string clientIp, CancellationToken cancellationToken)
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
            await Socks5RequestAsync(clientSocket, clientStream, clientIp, cancellationToken);
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

    private async Task Socks5RequestAsync(Socket clientSocket, NetworkStream clientStream, string clientIp, CancellationToken cancellationToken)
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

        await Socks5ConnectAsync(clientSocket, clientStream, targetHost, targetPort, clientIp, cancellationToken);
    }

    private async Task Socks5ConnectAsync(Socket clientSocket, NetworkStream clientStream,
        string targetHost, int targetPort, string clientIp, CancellationToken cancellationToken)
    {
        Socket? targetSocket = null;
        try
        {
            targetSocket = await ConnectTargetAsync(targetHost, targetPort, cancellationToken);

            await Socks5ReplyAsync(clientStream, REP_SUCCESS, cancellationToken);
            _logger.Debug("SOCKS5 连接成功: {Host}:{Port}", targetHost, targetPort);

            // SOCKS5 Pcap：目标端口为 443 且启用 Pcap 时，走 Pcap 路径
            if (_portConfig.EnablePcap && _pcapCertProvider != null && targetPort == 443
                && !IsPcapFallback(clientIp, targetHost))
            {
                using var targetNetStream = new NetworkStream(targetSocket, ownsSocket: false);

                // 与目标建立真实 TLS
                var targetSsl = new SslStream(targetNetStream, leaveInnerStreamOpen: true);
                await targetSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                }, cancellationToken);

                // 用伪造证书与客户端建立 TLS
                var fakeCert = _pcapCertProvider.GetCertForHost(targetHost);
                var clientSsl = new SslStream(clientStream, leaveInnerStreamOpen: true);
                await clientSsl.AuthenticateAsServerAsync(fakeCert, false, false);

                _logger.Debug("SOCKS5 Pcap 已建立: {Host}:{Port}", targetHost, targetPort);

                await PcapRelayAsync(clientSsl, targetSsl, targetHost, clientIp, cancellationToken);

                await clientSsl.ShutdownAsync();
                await targetSsl.ShutdownAsync();
            }
            else
            {
                using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
                await TunnelAsync(clientStream, targetStream, cancellationToken);
            }
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

    /// <summary>
    /// 在代理端口上直接提供 CRL 文件
    /// </summary>
    private async Task ServeCrlAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            var crlPath = _pcapCertProvider!.CaCrlPath;
            if (File.Exists(crlPath))
            {
                var crlBytes = await File.ReadAllBytesAsync(crlPath, cancellationToken);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-crl\r\nContent-Length: {crlBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
                await stream.WriteAsync(crlBytes, cancellationToken);
            }
            else
            {
                var response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(response, cancellationToken);
            }
            await stream.FlushAsync(cancellationToken);
        }
        catch { }
    }

    /// <summary>
    /// 在代理端口上直接提供 CA 证书文件（供客户端下载安装）
    /// </summary>
    private async Task ServeCaCertAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            var certPath = _pcapCertProvider!.CaCertPath;
            if (File.Exists(certPath))
            {
                var certBytes = await File.ReadAllBytesAsync(certPath, cancellationToken);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\nContent-Disposition: attachment; filename=\"lywaf-proxy-ca.crt\"\r\nContent-Length: {certBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
                await stream.WriteAsync(certBytes, cancellationToken);
            }
            else
            {
                var response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
                await stream.WriteAsync(response, cancellationToken);
            }
            await stream.FlushAsync(cancellationToken);
        }
        catch { }
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

    // ══════════════════════════════════════════
    //  Pcap / SslStream 辅助方法
    // ══════════════════════════════════════════

    private static async Task<string?> ReadLineFromSslAsync(SslStream stream, CancellationToken cancellationToken)
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

    private static async Task<Dictionary<string, string>> ReadHeadersFromSslAsync(SslStream stream, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineFromSslAsync(stream, cancellationToken);
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

    /// <summary>
    /// 转发 chunked 编码的响应体
    /// </summary>
    private static async Task CopyChunkedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // 读取 chunk 大小行
            var sizeLine = await ReadLineFromStreamAsync(source, cancellationToken);
            if (string.IsNullOrEmpty(sizeLine)) break;

            // 写入 chunk 大小到客户端
            var sizeBytes = Encoding.UTF8.GetBytes(sizeLine + "\r\n");
            await destination.WriteAsync(sizeBytes, cancellationToken);

            // 解析 chunk 大小（忽略扩展）
            var semiIdx = sizeLine.IndexOf(';');
            var sizeStr = semiIdx >= 0 ? sizeLine[..semiIdx].Trim() : sizeLine.Trim();
            if (!int.TryParse(sizeStr, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
                break;

            if (chunkSize == 0)
            {
                // 终止 chunk：写入空行
                await destination.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
                break;
            }

            // 转发 chunk 数据
            await CopyBytesAsync(source, destination, chunkSize, cancellationToken);

            // 读取 chunk 后的 \r\n
            var trailingCrLf = new byte[2];
            await ReadExactAsync(source, trailingCrLf, cancellationToken);
            await destination.WriteAsync(trailingCrLf, cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<string?> ReadLineFromStreamAsync(Stream stream, CancellationToken cancellationToken)
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

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }
    }
}
