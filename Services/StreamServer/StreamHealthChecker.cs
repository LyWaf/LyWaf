using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NLog;

namespace LyWaf.Services.StreamServer;

/// <summary>
/// 上游服务器健康状态
/// </summary>
public class UpstreamHealth
{
    /// <summary>
    /// 上游服务器地址
    /// </summary>
    public string Upstream { get; set; } = "";

    /// <summary>
    /// 是否健康
    /// </summary>
    public bool IsHealthy { get; set; } = true;

    /// <summary>
    /// 连续失败次数
    /// </summary>
    public int FailureCount { get; set; } = 0;

    /// <summary>
    /// 连续成功次数
    /// </summary>
    public int SuccessCount { get; set; } = 0;

    /// <summary>
    /// 最后检查时间
    /// </summary>
    public DateTime LastCheckTime { get; set; } = DateTime.MinValue;

    /// <summary>
    /// 最后检查结果
    /// </summary>
    public bool LastCheckResult { get; set; } = true;

    /// <summary>
    /// 最后错误信息
    /// </summary>
    public string? LastError { get; set; }
}

/// <summary>
/// TCP 流代理健康检查器
/// 定期检查上游服务器的 TCP 连接可用性
/// </summary>
public class StreamHealthChecker : IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly StreamServerOptions _options;
    private readonly ConcurrentDictionary<string, UpstreamHealth> _healthStatus = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _checkTask;
    private bool _disposed;

    public StreamHealthChecker(StreamServerOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 启动健康检查
    /// </summary>
    public void Start()
    {
        if (_checkTask != null)
            return;

        // 收集所有需要检查的上游服务器
        var allUpstreams = new HashSet<string>();
        foreach (var stream in _options.Streams.Values)
        {
            foreach (var upstream in stream.Upstreams)
            {
                allUpstreams.Add(upstream);
            }
        }

        // 初始化健康状态
        foreach (var upstream in allUpstreams)
        {
            _healthStatus.TryAdd(upstream, new UpstreamHealth
            {
                Upstream = upstream,
                IsHealthy = true  // 初始假设健康
            });
        }

        // 启动检查任务
        _checkTask = Task.Run(async () => await RunHealthCheckLoopAsync(_cts.Token));

        _logger.Info("Stream 健康检查已启动，检查间隔: {Interval}s", _options.HealthCheckInterval);
    }

    /// <summary>
    /// 停止健康检查
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        _checkTask?.Wait(TimeSpan.FromSeconds(5));
        _logger.Info("Stream 健康检查已停止");
    }

    /// <summary>
    /// 获取上游服务器的健康状态
    /// </summary>
    public bool IsHealthy(string upstream)
    {
        if (_healthStatus.TryGetValue(upstream, out var health))
        {
            return health.IsHealthy;
        }
        return true; // 未知的上游默认认为健康
    }

    /// <summary>
    /// 获取健康的上游服务器列表
    /// </summary>
    public List<string> GetHealthyUpstreams(IEnumerable<string> upstreams)
    {
        var healthy = upstreams.Where(u => IsHealthy(u)).ToList();

        // 如果所有上游都不健康，返回所有上游（让请求有机会尝试）
        if (healthy.Count == 0)
        {
            return upstreams.ToList();
        }

        return healthy;
    }

    /// <summary>
    /// 获取所有上游的健康状态
    /// </summary>
    public Dictionary<string, UpstreamHealth> GetAllHealthStatus()
    {
        return new Dictionary<string, UpstreamHealth>(_healthStatus);
    }

    /// <summary>
    /// 手动标记上游为不健康（例如连接失败时）
    /// </summary>
    public void MarkUnhealthy(string upstream, string? error = null)
    {
        if (_healthStatus.TryGetValue(upstream, out var health))
        {
            health.SuccessCount = 0;
            health.FailureCount++;
            health.LastError = error;

            if (health.FailureCount >= _options.UnhealthyThreshold)
            {
                if (health.IsHealthy)
                {
                    health.IsHealthy = false;
                    _logger.Warn("Stream 上游 {Upstream} 标记为不健康: {Error}", upstream, error ?? "连接失败");
                }
            }
        }
    }

    /// <summary>
    /// 手动标记上游为健康（例如连接成功时）
    /// </summary>
    public void MarkHealthy(string upstream)
    {
        if (_healthStatus.TryGetValue(upstream, out var health))
        {
            health.FailureCount = 0;
            health.SuccessCount++;
            health.LastError = null;

            if (health.SuccessCount >= _options.HealthyThreshold)
            {
                if (!health.IsHealthy)
                {
                    health.IsHealthy = true;
                    _logger.Info("Stream 上游 {Upstream} 恢复健康", upstream);
                }
            }
        }
    }

    /// <summary>
    /// 健康检查循环
    /// </summary>
    private async Task RunHealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        // 等待一小段时间后开始第一次检查
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllUpstreamsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stream 健康检查出错");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.HealthCheckInterval), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 检查所有上游服务器
    /// </summary>
    private async Task CheckAllUpstreamsAsync(CancellationToken cancellationToken)
    {
        var checkTasks = new List<Task>();

        foreach (var kvp in _healthStatus)
        {
            checkTasks.Add(CheckUpstreamAsync(kvp.Key, kvp.Value, cancellationToken));
        }

        await Task.WhenAll(checkTasks);
    }

    /// <summary>
    /// 检查单个上游服务器
    /// </summary>
    private async Task CheckUpstreamAsync(string upstream, UpstreamHealth health, CancellationToken cancellationToken)
    {
        var (host, port) = ParseHostPort(upstream);
        if (host == null || port == 0)
        {
            return;
        }

        var success = false;
        string? error = null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.HealthCheckTimeout));

            // DNS 解析
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
                if (addresses.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }
                targetIp = addresses[0];
            }

            // TCP 连接测试
            using var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(targetIp, port), cts.Token);
            success = true;
        }
        catch (OperationCanceledException)
        {
            error = "连接超时";
        }
        catch (SocketException ex)
        {
            error = ex.SocketErrorCode.ToString();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 更新健康状态
        health.LastCheckTime = DateTime.UtcNow;
        health.LastCheckResult = success;

        if (success)
        {
            health.FailureCount = 0;
            health.SuccessCount++;
            health.LastError = null;

            if (!health.IsHealthy && health.SuccessCount >= _options.HealthyThreshold)
            {
                health.IsHealthy = true;
                _logger.Info("Stream 上游 {Upstream} 恢复健康", upstream);
            }
        }
        else
        {
            health.SuccessCount = 0;
            health.FailureCount++;
            health.LastError = error;

            if (health.IsHealthy && health.FailureCount >= _options.UnhealthyThreshold)
            {
                health.IsHealthy = false;
                _logger.Warn("Stream 上游 {Upstream} 标记为不健康: {Error}", upstream, error);
            }
        }
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
