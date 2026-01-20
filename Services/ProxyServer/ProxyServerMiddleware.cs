using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.ProxyServer;

/// <summary>
/// HTTP/HTTPS 代理中间件
/// 处理 HTTP CONNECT 方法（HTTPS 隧道）和普通 HTTP 代理请求
/// </summary>
public class ProxyServerMiddleware
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ProxyServerOptions> _optionsMonitor;

    public ProxyServerMiddleware(RequestDelegate next, IOptionsMonitor<ProxyServerOptions> optionsMonitor)
    {
        _next = next;
        _optionsMonitor = optionsMonitor;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _optionsMonitor.CurrentValue;
        
        // 检查是否启用代理服务
        if (!options.Enabled)
        {
            await _next(context);
            return;
        }

        var localPort = context.Connection.LocalPort;
        var localIp = context.Connection.LocalIpAddress?.ToString() ?? "0.0.0.0";
        
        // 获取当前端口的代理配置
        // 优先级: host:port > 0.0.0.0:port > port > Default
        var portConfig = GetPortConfig(options, localIp, localPort);

        // 检查是否是代理请求
        var request = context.Request;
        
        // CONNECT 方法 - HTTPS 代理隧道
        if (request.Method == "CONNECT" && portConfig.EnableHttps)
        {
            await HandleConnectAsync(context, options, portConfig);
            return;
        }

        // 检查是否是 HTTP 代理请求（请求 URI 是绝对路径）
        var rawTarget = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        if (portConfig.EnableHttp && rawTarget != null && 
            (rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             rawTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            await HandleHttpProxyAsync(context, options, portConfig, rawTarget);
            return;
        }

        // 不是代理请求，继续正常处理
        await _next(context);
    }

    /// <summary>
    /// 处理 CONNECT 方法（HTTPS 隧道）
    /// </summary>
    private async Task HandleConnectAsync(HttpContext context, ProxyServerOptions options, ProxyPortConfig portConfig)
    {
        var request = context.Request;
        var targetHost = request.Host.Host;
        var targetPort = request.Host.Port ?? 443;

        _logger.Debug("代理 CONNECT 请求: {Host}:{Port}", targetHost, targetPort);

        // 认证检查
        if (portConfig.RequireAuth && !await CheckAuthAsync(context, options))
        {
            context.Response.StatusCode = 407;
            context.Response.Headers["Proxy-Authenticate"] = "Basic realm=\"Proxy\"";
            await context.Response.WriteAsync("Proxy Authentication Required");
            return;
        }

        // 检查目标主机是否允许
        if (!IsHostAllowed(targetHost, options))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: Target host not allowed");
            return;
        }

        try
        {
            // 连接目标服务器
            using var targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            targetSocket.NoDelay = true;

            var connectTimeout = TimeSpan.FromSeconds(options.ConnectTimeout);
            using var cts = new CancellationTokenSource(connectTimeout);

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

            // 返回 200 Connection Established
            context.Response.StatusCode = 200;
            await context.Response.Body.FlushAsync();

            // 获取底层连接进行双向数据转发
            var clientStream = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpUpgradeFeature>();
            if (clientStream == null || !clientStream.IsUpgradableRequest)
            {
                // 使用 Response.Body 和 Request.Body 进行转发
                await TunnelDataAsync(context, targetSocket, options);
            }
            else
            {
                // 升级连接进行隧道转发
                using var upgradedStream = await clientStream.UpgradeAsync();
                using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
                await TunnelStreamsAsync(upgradedStream, targetStream, options);
            }

            _logger.Debug("代理 CONNECT 完成: {Host}:{Port}", targetHost, targetPort);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("代理连接超时: {Host}:{Port}", targetHost, targetPort);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 504;
                await context.Response.WriteAsync("Gateway Timeout");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理 CONNECT 失败: {Host}:{Port}", targetHost, targetPort);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Bad Gateway");
            }
        }
    }

    /// <summary>
    /// 处理普通 HTTP 代理请求
    /// </summary>
    private async Task HandleHttpProxyAsync(HttpContext context, ProxyServerOptions options, ProxyPortConfig portConfig, string targetUrl)
    {
        _logger.Debug("代理 HTTP 请求: {Method} {Url}", context.Request.Method, targetUrl);

        // 认证检查
        if (portConfig.RequireAuth && !await CheckAuthAsync(context, options))
        {
            context.Response.StatusCode = 407;
            context.Response.Headers["Proxy-Authenticate"] = "Basic realm=\"Proxy\"";
            await context.Response.WriteAsync("Proxy Authentication Required");
            return;
        }

        try
        {
            var uri = new Uri(targetUrl);
            
            // 检查目标主机是否允许
            if (!IsHostAllowed(uri.Host, options))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: Target host not allowed");
                return;
            }

            // 创建转发请求
            using var httpClient = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeout),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(options.DataTimeout),
                // 使用自定义 DNS
                ConnectCallback = Dns.CustomDnsConnectCallbackFactory.Create()
            });

            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = uri
            };

            // 复制请求头（排除代理相关头）
            foreach (var header in context.Request.Headers)
            {
                var key = header.Key;
                if (key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                if (!requestMessage.Headers.TryAddWithoutValidation(key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(key, header.Value.ToArray());
                }
            }

            // 复制请求体
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                requestMessage.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType != null)
                {
                    requestMessage.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
                }
            }

            // 发送请求
            using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

            // 设置响应状态码
            context.Response.StatusCode = (int)response.StatusCode;

            // 复制响应头
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in response.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // 移除 Transfer-Encoding（ASP.NET Core 会自动处理）
            context.Response.Headers.Remove("Transfer-Encoding");

            // 复制响应体
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);

            _logger.Debug("代理 HTTP 完成: {StatusCode} {Url}", (int)response.StatusCode, targetUrl);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "代理 HTTP 失败: {Url}", targetUrl);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Bad Gateway");
            }
        }
    }

    /// <summary>
    /// 隧道数据转发（使用 Request/Response Body）
    /// </summary>
    private async Task TunnelDataAsync(HttpContext context, Socket targetSocket, ProxyServerOptions options)
    {
        using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
        var dataTimeout = TimeSpan.FromSeconds(options.DataTimeout);

        var clientToTarget = Task.Run(async () =>
        {
            try
            {
                var buffer = new byte[8192];
                while (true)
                {
                    using var cts = new CancellationTokenSource(dataTimeout);
                    var bytesRead = await context.Request.Body.ReadAsync(buffer, cts.Token);
                    if (bytesRead == 0) break;
                    await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                }
            }
            catch { }
        });

        var targetToClient = Task.Run(async () =>
        {
            try
            {
                var buffer = new byte[8192];
                while (true)
                {
                    using var cts = new CancellationTokenSource(dataTimeout);
                    var bytesRead = await targetStream.ReadAsync(buffer, cts.Token);
                    if (bytesRead == 0) break;
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                    await context.Response.Body.FlushAsync(cts.Token);
                }
            }
            catch { }
        });

        await Task.WhenAny(clientToTarget, targetToClient);
    }

    /// <summary>
    /// 双向流隧道转发
    /// </summary>
    private async Task TunnelStreamsAsync(Stream clientStream, Stream targetStream, ProxyServerOptions options)
    {
        var dataTimeout = TimeSpan.FromSeconds(options.DataTimeout);

        var clientToTarget = CopyStreamAsync(clientStream, targetStream, dataTimeout);
        var targetToClient = CopyStreamAsync(targetStream, clientStream, dataTimeout);

        await Task.WhenAny(clientToTarget, targetToClient);
    }

    private static async Task CopyStreamAsync(Stream source, Stream destination, TimeSpan timeout)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                using var cts = new CancellationTokenSource(timeout);
                var bytesRead = await source.ReadAsync(buffer, cts.Token);
                if (bytesRead == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                await destination.FlushAsync(cts.Token);
            }
        }
        catch { }
    }

    /// <summary>
    /// 检查认证
    /// </summary>
    private Task<bool> CheckAuthAsync(HttpContext context, ProxyServerOptions options)
    {
        if (string.IsNullOrEmpty(options.Username))
        {
            return Task.FromResult(true);
        }

        var authHeader = context.Request.Headers["Proxy-Authorization"].FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader))
        {
            return Task.FromResult(false);
        }

        // 解析 Basic 认证
        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encodedCredentials = authHeader[6..];
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
                var parts = credentials.Split(':', 2);
                if (parts.Length == 2)
                {
                    var username = parts[0];
                    var password = parts[1];
                    return Task.FromResult(username == options.Username && password == options.Password);
                }
            }
            catch
            {
                // 解析失败
            }
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// 检查目标主机是否允许
    /// </summary>
    private static bool IsHostAllowed(string host, ProxyServerOptions options)
    {
        // 检查黑名单
        foreach (var blocked in options.BlockedHosts)
        {
            if (MatchHost(host, blocked))
            {
                return false;
            }
        }

        // 如果有白名单，检查是否在白名单中
        if (options.AllowedHosts.Count > 0)
        {
            foreach (var allowed in options.AllowedHosts)
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

    /// <summary>
    /// 匹配主机（支持通配符）
    /// </summary>
    private static bool MatchHost(string host, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..]; // 包含点
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                   host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }
        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取端口配置
    /// 优先级: host:port > 0.0.0.0:port > port > Default
    /// </summary>
    private static ProxyPortConfig GetPortConfig(ProxyServerOptions options, string localIp, int localPort)
    {
        // 1. 精确匹配 host:port
        var exactKey = $"{localIp}:{localPort}";
        if (options.Ports.TryGetValue(exactKey, out var config))
        {
            return config;
        }

        // 2. 匹配 0.0.0.0:port（通配地址）
        var wildcardKey = $"0.0.0.0:{localPort}";
        if (localIp != "0.0.0.0" && options.Ports.TryGetValue(wildcardKey, out config))
        {
            return config;
        }

        // 3. 只匹配端口号
        if (options.Ports.TryGetValue(localPort.ToString(), out config))
        {
            return config;
        }

        // 4. 使用默认配置
        return options.Default;
    }
}
