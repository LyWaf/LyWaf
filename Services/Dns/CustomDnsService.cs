using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.Dns;

/// <summary>
/// 自定义 DNS 连接回调工厂
/// 用于在 ConnectCallback 中延迟获取 DNS 服务
/// </summary>
public static class CustomDnsConnectCallbackFactory
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 创建自定义 DNS 连接回调
    /// 在回调被调用时通过 ServiceLocator 获取 DNS 服务，支持配置热更新
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create()
    {
        return async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            IPAddress? resolvedIp = null;

            // 通过 ServiceLocator 获取 DNS 服务（支持配置热更新）
            var dnsService = Services.ServiceLocator.GetService<ICustomDnsService>();
            if (dnsService != null)
            {
                resolvedIp = await dnsService.ResolveAsync(host, cancellationToken);
            }

            Socket socket;

            if (resolvedIp != null)
            {
                // 使用自定义解析的 IP
                socket = new Socket(resolvedIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(resolvedIp, port), cancellationToken);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            else
            {
                // 使用系统默认 DNS 解析
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            return new NetworkStream(socket, ownsSocket: true);
        };
    }
}

/// <summary>
/// 自定义 DNS 解析服务
/// 支持将指定域名解析为指定的 IP 地址列表，随机或轮询选择
/// </summary>
public interface ICustomDnsService
{
    /// <summary>
    /// 解析域名为 IP 地址
    /// </summary>
    /// <param name="host">域名</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>解析后的 IP 地址，如果无法解析则返回 null</returns>
    Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查是否有自定义 DNS 映射
    /// </summary>
    /// <param name="host">域名</param>
    /// <returns>是否有匹配的自定义映射</returns>
    bool HasCustomMapping(string host);

    /// <summary>
    /// 获取自定义 DNS 连接回调
    /// 用于 SocketsHttpHandler.ConnectCallback
    /// </summary>
    Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> GetConnectCallback();
}

public class CustomDnsService : ICustomDnsService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IOptionsMonitor<CustomDnsOptions> _optionsMonitor;
    private readonly ConcurrentDictionary<string, (IPAddress[] addresses, DateTime expiry)> _cache = new();
    private readonly ConcurrentDictionary<string, int> _roundRobinCounters = new();
    
    // 预处理的映射表：精确匹配和通配符匹配
    private Dictionary<string, DnsEntry> _exactEntries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DnsEntry> _wildcardEntries = new(StringComparer.OrdinalIgnoreCase);
    private CustomDnsOptions _currentOptions;

    public CustomDnsService(IOptionsMonitor<CustomDnsOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
        _currentOptions = optionsMonitor.CurrentValue;
        
        // 初始化映射表
        RebuildMappings(_currentOptions);
        
        // 监听配置变化
        _optionsMonitor.OnChange(options =>
        {
            _logger.Info("检测到 CustomDns 配置变化，重新加载映射表...");
            _currentOptions = options;
            RebuildMappings(options);
            // 清除缓存
            _cache.Clear();
            _roundRobinCounters.Clear();
        });
    }

    /// <summary>
    /// 重建映射表，分离精确匹配和通配符匹配
    /// </summary>
    private void RebuildMappings(CustomDnsOptions options)
    {
        var exact = new Dictionary<string, DnsEntry>(StringComparer.OrdinalIgnoreCase);
        var wildcard = new Dictionary<string, DnsEntry>(StringComparer.OrdinalIgnoreCase);

        if (options.Enabled && options.Entries.Count > 0)
        {
            foreach (var entry in options.Entries)
            {
                var key = entry.Key;
                if (key.StartsWith("*."))
                {
                    // 通配符条目：*.example.com -> 存储为 example.com（去掉 *.）
                    var wildcardKey = key[2..]; // 去掉 "*."
                    wildcard[wildcardKey] = entry.Value;
                    _logger.Debug("  通配符映射: {Pattern} -> [{Addresses}] ({Policy})", 
                        key, 
                        string.Join(", ", entry.Value.Addresses),
                        entry.Value.Policy);
                }
                else
                {
                    // 精确匹配条目
                    exact[key] = entry.Value;
                    _logger.Debug("  精确映射: {Domain} -> [{Addresses}] ({Policy})", 
                        key, 
                        string.Join(", ", entry.Value.Addresses),
                        entry.Value.Policy);
                }
            }

            _logger.Info("自定义 DNS 服务已启用，共 {ExactCount} 条精确规则，{WildcardCount} 条通配符规则", 
                exact.Count, wildcard.Count);
        }

        // 原子更新
        _exactEntries = exact;
        _wildcardEntries = wildcard;
    }

    public bool HasCustomMapping(string host)
    {
        if (!_currentOptions.Enabled)
            return false;

        return FindMatchingEntry(host) != null;
    }

    public async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (!_currentOptions.Enabled)
            return null;

        // 查找匹配的自定义映射
        var entry = FindMatchingEntry(host);
        if (entry != null)
        {
            var ip = SelectAddress(host, entry);
            if (ip != null)
            {
                _logger.Debug("自定义 DNS 解析: {Host} -> {IP}", host, ip);
                return ip;
            }
        }

        // 没有自定义映射，返回 null 让调用方使用默认解析
        return null;
    }

    /// <summary>
    /// 获取自定义 DNS 连接回调
    /// </summary>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> GetConnectCallback()
    {
        return async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            IPAddress? resolvedIp = null;

            // 尝试使用自定义 DNS 解析
            if (_currentOptions.Enabled)
            {
                resolvedIp = await ResolveAsync(host, cancellationToken);
            }

            Socket socket;

            if (resolvedIp != null)
            {
                // 使用自定义解析的 IP
                socket = new Socket(resolvedIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(resolvedIp, port), cancellationToken);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            else
            {
                // 使用系统默认 DNS 解析
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            return new NetworkStream(socket, ownsSocket: true);
        };
    }

    /// <summary>
    /// 查找匹配的 DNS 条目
    /// 1. 先精确匹配 host
    /// 2. 再将 host 第一段替换为 * 进行通配符匹配
    /// </summary>
    private DnsEntry? FindMatchingEntry(string host)
    {
        // 1. 精确匹配（O(1) hash 查找）
        if (_exactEntries.TryGetValue(host, out var exactEntry))
        {
            return exactEntry;
        }

        // 2. 通配符匹配：将第一段替换为 * 
        // 例如: www.example.com -> example.com（查找 *.example.com 的配置）
        var dotIndex = host.IndexOf('.');
        if (dotIndex > 0 && dotIndex < host.Length - 1)
        {
            var wildcardKey = host[(dotIndex + 1)..]; // 去掉第一段，如 www.example.com -> example.com
            if (_wildcardEntries.TryGetValue(wildcardKey, out var wildcardEntry))
            {
                return wildcardEntry;
            }
        }

        return null;
    }

    private IPAddress? SelectAddress(string host, DnsEntry entry)
    {
        if (entry.Addresses.Count == 0)
            return null;

        // 检查缓存
        var cacheKey = $"{host}:{entry.GetHashCode()}";
        var ttl = entry.TtlSeconds >= 0 ? entry.TtlSeconds : _currentOptions.CacheTtlSeconds;

        if (ttl > 0 && _cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
        {
            // 从缓存的地址列表中选择
            return SelectFromAddresses(host, entry.Policy, cached.addresses);
        }

        // 解析所有地址
        var addresses = new List<IPAddress>();
        foreach (var addr in entry.Addresses)
        {
            if (IPAddress.TryParse(addr, out var ip))
            {
                addresses.Add(ip);
            }
            else
            {
                _logger.Warn("无效的 IP 地址配置: {Address}", addr);
            }
        }

        if (addresses.Count == 0)
            return null;

        var addressArray = addresses.ToArray();

        // 缓存解析结果
        if (ttl > 0)
        {
            _cache[cacheKey] = (addressArray, DateTime.UtcNow.AddSeconds(ttl));
        }

        return SelectFromAddresses(host, entry.Policy, addressArray);
    }

    private IPAddress SelectFromAddresses(string host, string policy, IPAddress[] addresses)
    {
        if (addresses.Length == 1)
            return addresses[0];

        return policy.ToLower() switch
        {
            "roundrobin" => SelectRoundRobin(host, addresses),
            _ => SelectRandom(addresses) // 默认随机
        };
    }

    private static IPAddress SelectRandom(IPAddress[] addresses)
    {
        var index = Random.Shared.Next(addresses.Length);
        return addresses[index];
    }

    private IPAddress SelectRoundRobin(string host, IPAddress[] addresses)
    {
        var counter = _roundRobinCounters.AddOrUpdate(host, 0, (_, old) => old + 1);
        var index = counter % addresses.Length;
        return addresses[index];
    }
}
