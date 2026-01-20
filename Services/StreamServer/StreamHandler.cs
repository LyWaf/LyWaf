using System.Net;
using System.Net.Sockets;
using NLog;

namespace LyWaf.Services.StreamServer;

/// <summary>
/// TCP 流代理处理器
/// 处理 TCP 连接的转发
/// </summary>
public class StreamHandler
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private static readonly Random _random = new();

    private readonly StreamServerOptions _globalOptions;
    private readonly StreamConfig _streamConfig;
    private readonly string _listenKey;
    
    // 轮询计数器
    private int _roundRobinIndex = 0;

    public StreamHandler(StreamServerOptions globalOptions, StreamConfig streamConfig, string listenKey)
    {
        _globalOptions = globalOptions;
        _streamConfig = streamConfig;
        _listenKey = listenKey;
    }

    /// <summary>
    /// 处理 TCP 连接
    /// </summary>
    public async Task HandleAsync(Socket clientSocket, CancellationToken cancellationToken = default)
    {
        if (_streamConfig.Upstreams.Count == 0)
        {
            _logger.Warn("Stream {Listen} 没有配置上游服务器", _listenKey);
            return;
        }

        Socket? targetSocket = null;
        string? selectedUpstream = null;

        try
        {
            var connectTimeout = TimeSpan.FromSeconds(
                _streamConfig.ConnectTimeout ?? _globalOptions.ConnectTimeout);
            
            // 选择上游服务器
            var (targetHost, targetPort) = await SelectUpstreamAsync(connectTimeout, cancellationToken);
            if (targetHost == null)
            {
                _logger.Warn("Stream {Listen} 所有上游服务器不可用", _listenKey);
                return;
            }

            selectedUpstream = $"{targetHost}:{targetPort}";
            _logger.Debug("Stream {Listen} -> {Upstream}", _listenKey, selectedUpstream);

            // 连接目标服务器
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

            // 创建到目标的连接
            targetSocket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            await targetSocket.ConnectAsync(new IPEndPoint(targetIp, targetPort), cts.Token);

            _logger.Debug("Stream {Listen} 已连接到 {Upstream} ({IP})", _listenKey, selectedUpstream, targetIp);

            // 开始双向数据转发
            using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);

            await TunnelAsync(clientStream, targetStream, cancellationToken);

            _logger.Debug("Stream {Listen} -> {Upstream} 连接结束", _listenKey, selectedUpstream);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Stream {Listen} 连接取消", _listenKey);
        }
        catch (SocketException ex)
        {
            _logger.Warn("Stream {Listen} -> {Upstream} 连接失败: {Error}", 
                _listenKey, selectedUpstream ?? "unknown", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Stream {Listen} 处理失败", _listenKey);
        }
        finally
        {
            targetSocket?.Dispose();
        }
    }

    /// <summary>
    /// 选择上游服务器
    /// </summary>
    private async Task<(string? host, int port)> SelectUpstreamAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var upstreams = _streamConfig.Upstreams;
        
        switch (_streamConfig.Policy)
        {
            case StreamLoadBalancePolicy.Random:
                // 随机选择
                var shuffled = upstreams.OrderBy(_ => _random.Next()).ToList();
                return await TryConnectToAnyAsync(shuffled, timeout, cancellationToken);

            case StreamLoadBalancePolicy.First:
                // 按顺序尝试第一个可用的
                return await TryConnectToAnyAsync(upstreams, timeout, cancellationToken);

            case StreamLoadBalancePolicy.RoundRobin:
            default:
                // 轮询
                var startIndex = Interlocked.Increment(ref _roundRobinIndex) - 1;
                var orderedUpstreams = new List<string>();
                for (int i = 0; i < upstreams.Count; i++)
                {
                    orderedUpstreams.Add(upstreams[(startIndex + i) % upstreams.Count]);
                }
                return await TryConnectToAnyAsync(orderedUpstreams, timeout, cancellationToken);
        }
    }

    /// <summary>
    /// 尝试连接到任意一个上游服务器
    /// </summary>
    private static async Task<(string? host, int port)> TryConnectToAnyAsync(
        List<string> upstreams, TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var upstream in upstreams)
        {
            var (host, port) = ParseHostPort(upstream);
            if (host == null || port == 0)
            {
                continue;
            }

            // 简单检查：如果只有一个上游，直接返回，不做预连接测试
            if (upstreams.Count == 1)
            {
                return (host, port);
            }

            // 多个上游时，尝试快速连接测试
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(5, timeout.TotalSeconds / 2)));

                // 使用自定义 DNS 解析
                var dnsService = ServiceLocator.GetService<Dns.ICustomDnsService>();
                IPAddress? resolvedIp = null;
                if (dnsService != null)
                {
                    resolvedIp = await dnsService.ResolveAsync(host, cts.Token);
                }

                IPAddress targetIp;
                if (resolvedIp != null)
                {
                    targetIp = resolvedIp;
                }
                else
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cts.Token);
                    if (addresses.Length == 0) continue;
                    targetIp = addresses[0];
                }

                using var testSocket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await testSocket.ConnectAsync(new IPEndPoint(targetIp, port), cts.Token);
                
                // 连接成功，返回此上游
                return (host, port);
            }
            catch
            {
                // 连接失败，尝试下一个
                _logger.Debug("Stream 上游 {Upstream} 不可用，尝试下一个", upstream);
            }
        }

        // 所有上游都不可用，返回第一个让主逻辑处理错误
        if (upstreams.Count > 0)
        {
            var (host, port) = ParseHostPort(upstreams[0]);
            return (host, port);
        }

        return (null, 0);
    }

    /// <summary>
    /// 解析主机和端口
    /// </summary>
    private static (string? host, int port) ParseHostPort(string upstream)
    {
        var lastColon = upstream.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= upstream.Length - 1)
        {
            return (null, 0);
        }

        var host = upstream[..lastColon];
        if (!int.TryParse(upstream[(lastColon + 1)..], out var port) || port <= 0 || port > 65535)
        {
            return (null, 0);
        }

        // 处理 IPv6 地址（如 [::1]:8080）
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        return (host, port);
    }

    /// <summary>
    /// 双向数据隧道
    /// </summary>
    private async Task TunnelAsync(NetworkStream clientStream, NetworkStream targetStream, CancellationToken cancellationToken)
    {
        var dataTimeout = TimeSpan.FromSeconds(
            _streamConfig.DataTimeout ?? _globalOptions.DataTimeout);

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
}
