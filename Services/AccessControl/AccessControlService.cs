using System.Collections.Concurrent;
using IP2Region.Net.Abstractions;
using IP2Region.Net.XDB;
using LyWaf.Services.SpeedLimit;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.AccessControl;

/// <summary>
/// 访问控制服务接口
/// 整合 IP 访问控制、地理位置访问控制和连接限制
/// </summary>
public interface IAccessControlService
{
    /// <summary>
    /// 检查是否允许访问
    /// </summary>
    /// <param name="clientIp">客户端 IP</param>
    /// <param name="path">请求路径</param>
    /// <returns>访问检查结果</returns>
    AccessCheckResult CheckAccess(string clientIp, string? path = null);

    /// <summary>
    /// 获取地理位置信息
    /// </summary>
    /// <param name="ip">IP 地址</param>
    /// <returns>地理位置信息</returns>
    GeoInfo? GetGeoInfo(string ip);

    /// <summary>
    /// 获取配置选项
    /// </summary>
    AccessControlOptions GetOptions();

    /// <summary>
    /// 尝试获取连接许可
    /// </summary>
    /// <param name="clientIp">客户端 IP</param>
    /// <param name="destination">目标服务器地址</param>
    /// <param name="path">请求路径</param>
    /// <returns>如果获取成功返回 true，否则返回 false</returns>
    bool TryAcquireConnection(string clientIp, string? destination = null, string? path = null);

    /// <summary>
    /// 释放连接许可
    /// </summary>
    /// <param name="clientIp">客户端 IP</param>
    /// <param name="destination">目标服务器地址</param>
    /// <param name="path">请求路径</param>
    void ReleaseConnection(string clientIp, string? destination = null, string? path = null);

    /// <summary>
    /// 获取当前连接统计信息
    /// </summary>
    ConnectionStats GetConnectionStats();

    /// <summary>
    /// 动态添加 IP 到白名单
    /// </summary>
    /// <param name="ipOrCidr">IP 地址或 CIDR 格式</param>
    /// <returns>是否成功添加</returns>
    bool AddWhitelist(string ipOrCidr);

    /// <summary>
    /// 动态从白名单移除 IP
    /// </summary>
    /// <param name="ipOrCidr">IP 地址或 CIDR 格式</param>
    /// <returns>是否成功移除</returns>
    bool RemoveWhitelist(string ipOrCidr);

    /// <summary>
    /// 获取当前白名单列表
    /// </summary>
    /// <returns>白名单 IP/CIDR 列表</returns>
    List<string> GetWhitelist();

    /// <summary>
    /// 动态添加 IP 到黑名单
    /// </summary>
    /// <param name="ipOrCidr">IP 地址或 CIDR 格式</param>
    /// <returns>是否成功添加</returns>
    bool AddBlacklist(string ipOrCidr);

    /// <summary>
    /// 动态从黑名单移除 IP
    /// </summary>
    /// <param name="ipOrCidr">IP 地址或 CIDR 格式</param>
    /// <returns>是否成功移除</returns>
    bool RemoveBlacklist(string ipOrCidr);

    /// <summary>
    /// 获取当前黑名单列表
    /// </summary>
    /// <returns>黑名单 IP/CIDR 列表</returns>
    List<string> GetBlacklist();

    /// <summary>
    /// 添加禁止访问的国家/地区
    /// </summary>
    bool AddDenyCountry(string country);

    /// <summary>
    /// 移除禁止访问的国家/地区
    /// </summary>
    bool RemoveDenyCountry(string country);

    /// <summary>
    /// 获取禁止访问的国家/地区列表
    /// </summary>
    List<string> GetDenyCountries();

    /// <summary>
    /// 添加禁止访问的省份
    /// </summary>
    bool AddDenyRegion(string region);

    /// <summary>
    /// 移除禁止访问的省份
    /// </summary>
    bool RemoveDenyRegion(string region);

    /// <summary>
    /// 获取禁止访问的省份列表
    /// </summary>
    List<string> GetDenyRegions();

    /// <summary>
    /// 添加允许访问的国家/地区
    /// </summary>
    bool AddAllowCountry(string country);

    /// <summary>
    /// 移除允许访问的国家/地区
    /// </summary>
    bool RemoveAllowCountry(string country);

    /// <summary>
    /// 获取允许访问的国家/地区列表
    /// </summary>
    List<string> GetAllowCountries();

    /// <summary>
    /// 添加允许访问的省份
    /// </summary>
    bool AddAllowRegion(string region);

    /// <summary>
    /// 移除允许访问的省份
    /// </summary>
    bool RemoveAllowRegion(string region);

    /// <summary>
    /// 获取允许访问的省份列表
    /// </summary>
    List<string> GetAllowRegions();

    /// <summary>
    /// 设置 IP 访问控制启用状态
    /// </summary>
    void SetIpControlEnabled(bool enabled);

    /// <summary>
    /// 设置地理位置访问控制启用状态
    /// </summary>
    void SetGeoControlEnabled(bool enabled);
}

/// <summary>
/// 连接统计信息
/// </summary>
public class ConnectionStats
{
    public int TotalConnections { get; set; }
    public Dictionary<string, int> ConnectionsPerIp { get; set; } = [];
    public Dictionary<string, int> ConnectionsPerDestination { get; set; } = [];
    public Dictionary<string, int> ConnectionsPerPath { get; set; } = [];
}

/// <summary>
/// 访问检查结果
/// </summary>
public class AccessCheckResult
{
    /// <summary>
    /// 是否允许访问
    /// </summary>
    public bool IsAllowed { get; set; } = true;

    /// <summary>
    /// 拒绝原因
    /// </summary>
    public AccessDenyReason DenyReason { get; set; } = AccessDenyReason.None;

    /// <summary>
    /// 拒绝时返回的 HTTP 状态码
    /// </summary>
    public int RejectStatusCode { get; set; } = 403;

    /// <summary>
    /// 拒绝时返回的消息
    /// </summary>
    public string RejectMessage { get; set; } = "Access Denied";

    /// <summary>
    /// 地理位置信息（如果有）
    /// </summary>
    public GeoInfo? GeoInfo { get; set; }

    /// <summary>
    /// 创建允许结果
    /// </summary>
    public static AccessCheckResult Allowed() => new() { IsAllowed = true };

    /// <summary>
    /// 创建拒绝结果
    /// </summary>
    public static AccessCheckResult Denied(AccessDenyReason reason, int statusCode, string message, GeoInfo? geoInfo = null)
        => new()
        {
            IsAllowed = false,
            DenyReason = reason,
            RejectStatusCode = statusCode,
            RejectMessage = message,
            GeoInfo = geoInfo
        };
}

/// <summary>
/// 拒绝原因
/// </summary>
public enum AccessDenyReason
{
    /// <summary>
    /// 无（允许访问）
    /// </summary>
    None,

    /// <summary>
    /// IP 被拒绝
    /// </summary>
    IpDenied,

    /// <summary>
    /// 路径 IP 规则被拒绝
    /// </summary>
    PathIpDenied,

    /// <summary>
    /// 地理位置被拒绝
    /// </summary>
    GeoDenied,

    /// <summary>
    /// 路径地理位置规则被拒绝
    /// </summary>
    PathGeoDenied
}

/// <summary>
/// 地理位置信息
/// </summary>
public class GeoInfo
{
    /// <summary>
    /// 国家名称（如：中国、美国）
    /// </summary>
    public string Country { get; set; } = "";

    /// <summary>
    /// 地区/省份（如：北京、广东省）
    /// </summary>
    public string Region { get; set; } = "";

    /// <summary>
    /// 城市（如：北京市、深圳市）
    /// </summary>
    public string City { get; set; } = "";

    /// <summary>
    /// ISP 运营商（如：电信、联通）
    /// </summary>
    public string Isp { get; set; } = "";

    /// <summary>
    /// 原始查询结果
    /// </summary>
    public string RawResult { get; set; } = "";
}

/// <summary>
/// 访问控制服务实现
/// 整合 IP 访问控制和地理位置访问控制
/// </summary>
public class AccessControlService : IAccessControlService, IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private AccessControlOptions _options;
    private ISearcher? _geoSearcher;
    private bool _geoInitialized = false;

    // IP 访问控制缓存
    private List<IpNetwork> _whitelistNetworks = [];
    private List<IpNetwork> _blacklistNetworks = [];
    private Dictionary<string, (List<IpNetwork> Whitelist, List<IpNetwork> Blacklist)> _pathIpRules = [];

    // 动态添加的白名单和黑名单（运行时添加，不持久化）
    private readonly HashSet<string> _dynamicWhitelist = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicDenyCountries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicDenyRegions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicAllowCountries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicAllowRegions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dynamicListLock = new();

    // 连接计数器
    private int _totalConnections = 0;
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new();
    private readonly ConcurrentDictionary<string, int> _connectionsPerDestination = new();
    private readonly ConcurrentDictionary<string, int> _connectionsPerPath = new();
    private readonly object _connectionLock = new();

    public AccessControlService(IOptionsMonitor<AccessControlOptions> options)
    {
        _options = options.CurrentValue;

        // 订阅配置变更
        options.OnChange(newConfig =>
        {
            _options = newConfig;
            BuildIpNetworks();
        });

        BuildIpNetworks();
        InitializeGeoService();
    }

    /// <summary>
    /// 初始化地理位置服务
    /// </summary>
    private void InitializeGeoService()
    {
        var geoConfig = _options.GeoControl;
        if (!geoConfig.Enabled)
        {
            _logger.Info("地理位置访问控制未启用");
            return;
        }

        try
        {
            if (!File.Exists(geoConfig.DatabasePath))
            {
                _logger.Warn("IP2Region 数据库文件不存在: {Path}，地理位置访问控制将被禁用", geoConfig.DatabasePath);
                return;
            }

            // 使用完全基于内存的查询（最快）
            _geoSearcher = new Searcher(CachePolicy.Content, geoConfig.DatabasePath);
            _geoInitialized = true;
            _logger.Info("地理位置访问控制已初始化，数据库: {Path}", geoConfig.DatabasePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "初始化地理位置服务失败: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// 构建 IP 访问控制网络列表
    /// </summary>
    private void BuildIpNetworks()
    {
        var ipConfig = _options.IpControl;

        // 合并配置中的白名单和动态添加的白名单
        lock (_dynamicListLock)
        {
            var allWhitelist = new List<string>(_options.Whitelist);
            allWhitelist.AddRange(_dynamicWhitelist);
            _whitelistNetworks = ParseNetworks(allWhitelist);
        }

        // 合并配置中的黑名单和动态添加的黑名单
        lock (_dynamicListLock)
        {
            var allBlacklist = new List<string>(ipConfig.Blacklist);
            allBlacklist.AddRange(_dynamicBlacklist);
            _blacklistNetworks = ParseNetworks(allBlacklist);
        }

        // 解析路径规则
        _pathIpRules = [];
        foreach (var rule in ipConfig.PathRules)
        {
            var allowNetworks = ParseNetworks(rule.Value.Whitelist);
            var denyNetworks = ParseNetworks(rule.Value.Blacklist);
            _pathIpRules[rule.Key] = (allowNetworks, denyNetworks);
        }

        _logger.Info("IP 访问控制规则已更新: 白名单 {WhiteCount} 条, 黑名单 {BlackCount} 条, 路径规则 {PathCount} 条",
            _whitelistNetworks.Count, _blacklistNetworks.Count, _pathIpRules.Count);
    }

    /// <summary>
    /// 检查是否允许访问
    /// </summary>
    public AccessCheckResult CheckAccess(string clientIp, string? path = null)
    {
        // 1. 首先检查 IP 白名单（白名单直接通过，不检查 GeoIP）
        if (IsIpInNetworks(clientIp, _whitelistNetworks))
        {
            _logger.Debug("IP {ClientIp} 在白名单中，跳过所有访问控制检查", clientIp);
            return AccessCheckResult.Allowed();
        }

        // 2. IP 访问控制检查
        if (_options.IpControl.Enabled)
        {
            var ipCheckResult = CheckIpAccess(clientIp, path);
            if (!ipCheckResult.IsAllowed)
                return ipCheckResult;
        }

        // 3. 地理位置访问控制检查
        if (_options.GeoControl.Enabled && _geoInitialized)
        {
            var geoCheckResult = CheckGeoAccess(clientIp, path);
            if (!geoCheckResult.IsAllowed)
                return geoCheckResult;
        }

        return AccessCheckResult.Allowed();
    }

    /// <summary>
    /// 检查 IP 访问控制
    /// </summary>
    private AccessCheckResult CheckIpAccess(string clientIp, string? path)
    {
        var ipConfig = _options.IpControl;

        // 检查路径级别的规则
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var rule in _pathIpRules)
            {
                if (MatchPath(path, rule.Key))
                {
                    // 路径规则的黑名单优先
                    if (IsIpInNetworks(clientIp, rule.Value.Blacklist))
                    {
                        return AccessCheckResult.Denied(
                            AccessDenyReason.PathIpDenied,
                            _options.RejectStatusCode,
                            _options.RejectMessage);
                    }

                    // 如果在白名单中，允许访问
                    if (IsIpInNetworks(clientIp, rule.Value.Whitelist))
                        return AccessCheckResult.Allowed();
                }
            }
        }

        // 检查全局黑名单
        if (IsIpInNetworks(clientIp, _blacklistNetworks))
        {
            return AccessCheckResult.Denied(
                AccessDenyReason.IpDenied,
                _options.RejectStatusCode,
                _options.RejectMessage);
        }

        return AccessCheckResult.Allowed();
    }

    /// <summary>
    /// 检查地理位置访问控制
    /// </summary>
    private AccessCheckResult CheckGeoAccess(string clientIp, string? path)
    {
        var geoConfig = _options.GeoControl;
        var geoInfo = GetGeoInfo(clientIp);

        if (geoInfo == null)
            return AccessCheckResult.Allowed(); // 查询失败时默认允许

        var rejectMessage = geoConfig.RejectMessage ?? _options.RejectMessage;

        // 检查路径级别的地理位置规则
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var rule in geoConfig.PathRules)
            {
                if (MatchPath(path, rule.Key))
                {
                    // 检查路径规则的黑名单
                    if (rule.Value.Blacklist.Count > 0 && IsCountryInList(geoInfo, rule.Value.Blacklist))
                    {
                        return AccessCheckResult.Denied(
                            AccessDenyReason.PathGeoDenied,
                            _options.RejectStatusCode,
                            rejectMessage,
                            geoInfo);
                    }
                    // 检查路径规则的白名单
                    if (rule.Value.Whitelist.Count > 0 && !IsCountryInList(geoInfo, rule.Value.Whitelist))
                    {
                        return AccessCheckResult.Denied(
                            AccessDenyReason.PathGeoDenied,
                            _options.RejectStatusCode,
                            rejectMessage,
                            geoInfo);
                    }
                }
            }
        }

        // 获取动态添加的规则
        List<string> dynamicDenyCountries;
        List<string> dynamicDenyRegions;
        List<string> dynamicAllowCountries;
        List<string> dynamicAllowRegions;
        lock (_dynamicListLock)
        {
            dynamicDenyCountries = _dynamicDenyCountries.ToList();
            dynamicDenyRegions = _dynamicDenyRegions.ToList();
            dynamicAllowCountries = _dynamicAllowCountries.ToList();
            dynamicAllowRegions = _dynamicAllowRegions.ToList();
        }

        // 检查动态添加的允许列表（如果有动态允许列表，优先检查）
        if (dynamicAllowCountries.Count > 0 || dynamicAllowRegions.Count > 0)
        {
            var isAllowed = false;

            // 检查国家是否在动态允许列表中
            if (dynamicAllowCountries.Count > 0 &&
                dynamicAllowCountries.Any(c => c.Equals(geoInfo.Country, StringComparison.OrdinalIgnoreCase)))
            {
                isAllowed = true;
            }

            // 检查省份是否在动态允许列表中
            if (!isAllowed && dynamicAllowRegions.Count > 0 &&
                dynamicAllowRegions.Any(r => r.Equals(geoInfo.Region, StringComparison.OrdinalIgnoreCase)))
            {
                isAllowed = true;
            }

            if (!isAllowed)
            {
                return AccessCheckResult.Denied(
                    AccessDenyReason.GeoDenied,
                    _options.RejectStatusCode,
                    rejectMessage,
                    geoInfo);
            }
        }

        // 检查动态添加的国家黑名单
        if (dynamicDenyCountries.Count > 0 &&
            dynamicDenyCountries.Any(c => c.Equals(geoInfo.Country, StringComparison.OrdinalIgnoreCase)))
        {
            return AccessCheckResult.Denied(
                AccessDenyReason.GeoDenied,
                _options.RejectStatusCode,
                rejectMessage,
                geoInfo);
        }

        // 检查动态添加的省份黑名单
        if (dynamicDenyRegions.Count > 0 &&
            dynamicDenyRegions.Any(r => r.Equals(geoInfo.Region, StringComparison.OrdinalIgnoreCase)))
        {
            return AccessCheckResult.Denied(
                AccessDenyReason.GeoDenied,
                _options.RejectStatusCode,
                rejectMessage,
                geoInfo);
        }

        // 检查全局地理位置规则
        if (geoConfig.Mode == GeoAccessMode.Allow)
        {
            // 允许模式：只有列表中的国家/省份可以访问
            var isAllowed = false;

            // 检查国家
            if (geoConfig.AllowCountries.Count > 0 && IsCountryInList(geoInfo, geoConfig.AllowCountries))
            {
                isAllowed = true;
            }

            // 检查省份（配置中的 AllowRegions）
            if (!isAllowed && geoConfig.AllowRegions.Count > 0 &&
                geoConfig.AllowRegions.Any(r => r.Equals(geoInfo.Region, StringComparison.OrdinalIgnoreCase)))
            {
                isAllowed = true;
            }

            // 如果都没有配置，允许访问
            if (!isAllowed && (geoConfig.AllowCountries.Count > 0 || geoConfig.AllowRegions.Count > 0))
            {
                return AccessCheckResult.Denied(
                    AccessDenyReason.GeoDenied,
                    _options.RejectStatusCode,
                    rejectMessage,
                    geoInfo);
            }
        }
        else
        {
            // 禁止模式：列表中的国家/省份被禁止
            if (geoConfig.DenyCountries.Count > 0 && IsCountryInList(geoInfo, geoConfig.DenyCountries))
            {
                return AccessCheckResult.Denied(
                    AccessDenyReason.GeoDenied,
                    _options.RejectStatusCode,
                    rejectMessage,
                    geoInfo);
            }

            // 检查省份黑名单（配置中的 DenyRegions）
            if (geoConfig.DenyRegions.Count > 0 &&
                geoConfig.DenyRegions.Any(r => r.Equals(geoInfo.Region, StringComparison.OrdinalIgnoreCase)))
            {
                return AccessCheckResult.Denied(
                    AccessDenyReason.GeoDenied,
                    _options.RejectStatusCode,
                    rejectMessage,
                    geoInfo);
            }
        }

        return AccessCheckResult.Allowed();
    }

    /// <summary>
    /// 获取地理位置信息
    /// </summary>
    public GeoInfo? GetGeoInfo(string ip)
    {
        if (!_geoInitialized || _geoSearcher == null)
            return null;

        try
        {
            var result = _geoSearcher.Search(ip);
            if (string.IsNullOrEmpty(result))
                return null;

            // IP2Region 返回格式: 国家|区域|省份|城市|ISP
            var parts = result.Split('|');
            return new GeoInfo
            {
                Country = parts.Length > 0 ? NormalizeValue(parts[0]) : "",
                Region = parts.Length > 2 ? NormalizeValue(parts[2]) : "",
                City = parts.Length > 3 ? NormalizeValue(parts[3]) : "",
                Isp = parts.Length > 4 ? NormalizeValue(parts[4]) : "",
                RawResult = result
            };
        }
        catch (Exception ex)
        {
            _logger.Debug("查询 IP 地理位置失败: {IP}, {Error}", ip, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 获取配置选项
    /// </summary>
    public AccessControlOptions GetOptions() => _options;

    /// <summary>
    /// 设置 IP 访问控制启用状态
    /// </summary>
    public void SetIpControlEnabled(bool enabled)
    {
        _options.IpControl.Enabled = enabled;
        _logger.Info("IP 访问控制已{Status}", enabled ? "启用" : "禁用");
    }

    /// <summary>
    /// 设置地理位置访问控制启用状态
    /// </summary>
    public void SetGeoControlEnabled(bool enabled)
    {
        _options.GeoControl.Enabled = enabled;
        _logger.Info("地理位置访问控制已{Status}", enabled ? "启用" : "禁用");
    }

    /// <summary>
    /// 检查国家/地区是否在列表中
    /// </summary>
    private static bool IsCountryInList(GeoInfo geoInfo, List<string> countries)
    {
        return countries.Any(c =>
            c.Equals(geoInfo.Country, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 规范化值，将 "0" 转为空字符串
    /// </summary>
    private static string NormalizeValue(string value)
    {
        return value == "0" ? "" : value;
    }

    /// <summary>
    /// 解析 CIDR 网络列表
    /// </summary>
    private static List<IpNetwork> ParseNetworks(List<string> cidrs)
    {
        var networks = new List<IpNetwork>();
        foreach (var cidr in cidrs)
        {
            if (IpNetwork.TryParse(cidr.Trim(), out var network) && network != null)
            {
                networks.Add(network);
            }
        }
        return networks;
    }

    /// <summary>
    /// 检查 IP 是否在网络列表中
    /// </summary>
    private static bool IsIpInNetworks(string clientIp, List<IpNetwork> networks)
    {
        if (string.IsNullOrEmpty(clientIp) || networks.Count == 0)
            return false;

        foreach (var network in networks)
        {
            if (network.Contains(clientIp))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 匹配路径，支持通配符
    /// </summary>
    private static bool MatchPath(string path, string pattern)
    {
        if (pattern.EndsWith("/*"))
        {
            var prefix = pattern[..^2];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        else if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 尝试获取连接许可
    /// </summary>
    public bool TryAcquireConnection(string clientIp, string? destination = null, string? path = null)
    {
        var connectionLimit = _options.ConnectionLimit;

        if (!connectionLimit.Enabled)
            return true;

        lock (_connectionLock)
        {
            // 检查全局连接数
            if (connectionLimit.MaxTotalConnections > 0 && _totalConnections >= connectionLimit.MaxTotalConnections)
            {
                _logger.Warn("全局连接数已达上限: {Current}/{Max}", _totalConnections, connectionLimit.MaxTotalConnections);
                return false;
            }

            // 检查每个 IP 的连接数
            if (connectionLimit.MaxConnectionsPerIp > 0 && !string.IsNullOrEmpty(clientIp))
            {
                var ipConnections = _connectionsPerIp.GetValueOrDefault(clientIp, 0);
                if (ipConnections >= connectionLimit.MaxConnectionsPerIp)
                {
                    _logger.Warn("IP {ClientIp} 连接数已达上限: {Current}/{Max}", clientIp, ipConnections, connectionLimit.MaxConnectionsPerIp);
                    return false;
                }
            }

            // 检查每个后端服务器的连接数
            if (connectionLimit.MaxConnectionsPerDestination > 0 && !string.IsNullOrEmpty(destination))
            {
                var destConnections = _connectionsPerDestination.GetValueOrDefault(destination, 0);
                if (destConnections >= connectionLimit.MaxConnectionsPerDestination)
                {
                    _logger.Warn("目标 {Destination} 连接数已达上限: {Current}/{Max}", destination, destConnections, connectionLimit.MaxConnectionsPerDestination);
                    return false;
                }
            }

            // 检查路径连接数
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var pathLimit in connectionLimit.PathLimits)
                {
                    if (MatchPath(path, pathLimit.Key))
                    {
                        var pathConnections = _connectionsPerPath.GetValueOrDefault(pathLimit.Key, 0);
                        if (pathConnections >= pathLimit.Value)
                        {
                            _logger.Warn("路径 {Path} 连接数已达上限: {Current}/{Max}", path, pathConnections, pathLimit.Value);
                            return false;
                        }
                        // 递增路径连接数
                        _connectionsPerPath.AddOrUpdate(pathLimit.Key, 1, (_, v) => v + 1);
                        break;
                    }
                }
            }

            // 递增连接计数
            _totalConnections++;
            if (!string.IsNullOrEmpty(clientIp))
            {
                _connectionsPerIp.AddOrUpdate(clientIp, 1, (_, v) => v + 1);
            }
            if (!string.IsNullOrEmpty(destination))
            {
                _connectionsPerDestination.AddOrUpdate(destination, 1, (_, v) => v + 1);
            }

            return true;
        }
    }

    /// <summary>
    /// 释放连接许可
    /// </summary>
    public void ReleaseConnection(string clientIp, string? destination = null, string? path = null)
    {
        var connectionLimit = _options.ConnectionLimit;

        if (!connectionLimit.Enabled)
            return;

        lock (_connectionLock)
        {
            // 递减全局连接数
            if (_totalConnections > 0)
                _totalConnections--;

            // 递减 IP 连接数
            if (!string.IsNullOrEmpty(clientIp))
            {
                _connectionsPerIp.AddOrUpdate(clientIp, 0, (_, v) => Math.Max(0, v - 1));
            }

            // 递减目标服务器连接数
            if (!string.IsNullOrEmpty(destination))
            {
                _connectionsPerDestination.AddOrUpdate(destination, 0, (_, v) => Math.Max(0, v - 1));
            }

            // 递减路径连接数
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var pathLimit in connectionLimit.PathLimits)
                {
                    if (MatchPath(path, pathLimit.Key))
                    {
                        _connectionsPerPath.AddOrUpdate(pathLimit.Key, 0, (_, v) => Math.Max(0, v - 1));
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 获取连接统计信息
    /// </summary>
    public ConnectionStats GetConnectionStats()
    {
        return new ConnectionStats
        {
            TotalConnections = _totalConnections,
            ConnectionsPerIp = new Dictionary<string, int>(_connectionsPerIp),
            ConnectionsPerDestination = new Dictionary<string, int>(_connectionsPerDestination),
            ConnectionsPerPath = new Dictionary<string, int>(_connectionsPerPath)
        };
    }

    /// <summary>
    /// 动态添加 IP 到白名单
    /// </summary>
    public bool AddWhitelist(string ipOrCidr)
    {
        if (string.IsNullOrWhiteSpace(ipOrCidr))
            return false;

        ipOrCidr = ipOrCidr.Trim();

        // 验证 IP 或 CIDR 格式
        if (!IpNetwork.TryParse(ipOrCidr, out _))
        {
            _logger.Warn("无效的 IP 或 CIDR 格式: {IpOrCidr}", ipOrCidr);
            return false;
        }

        lock (_dynamicListLock)
        {
            if (_dynamicWhitelist.Add(ipOrCidr))
            {
                BuildIpNetworks(); // 重新构建网络列表
                _logger.Info("已动态添加白名单: {IpOrCidr}", ipOrCidr);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 动态从白名单移除 IP
    /// </summary>
    public bool RemoveWhitelist(string ipOrCidr)
    {
        if (string.IsNullOrWhiteSpace(ipOrCidr))
            return false;

        ipOrCidr = ipOrCidr.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicWhitelist.Remove(ipOrCidr))
            {
                BuildIpNetworks(); // 重新构建网络列表
                _logger.Info("已从白名单移除: {IpOrCidr}", ipOrCidr);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取当前白名单列表
    /// </summary>
    public List<string> GetWhitelist()
    {
        lock (_dynamicListLock)
        {
            var allWhitelist = new List<string>(_options.Whitelist);
            allWhitelist.AddRange(_dynamicWhitelist);
            return allWhitelist.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// 动态添加 IP 到黑名单
    /// </summary>
    public bool AddBlacklist(string ipOrCidr)
    {
        if (string.IsNullOrWhiteSpace(ipOrCidr))
            return false;

        ipOrCidr = ipOrCidr.Trim();

        // 验证 IP 或 CIDR 格式
        if (!IpNetwork.TryParse(ipOrCidr, out _))
        {
            _logger.Warn("无效的 IP 或 CIDR 格式: {IpOrCidr}", ipOrCidr);
            return false;
        }

        lock (_dynamicListLock)
        {
            if (_dynamicBlacklist.Add(ipOrCidr))
            {
                BuildIpNetworks(); // 重新构建网络列表
                _logger.Info("已动态添加黑名单: {IpOrCidr}", ipOrCidr);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 动态从黑名单移除 IP
    /// </summary>
    public bool RemoveBlacklist(string ipOrCidr)
    {
        if (string.IsNullOrWhiteSpace(ipOrCidr))
            return false;

        ipOrCidr = ipOrCidr.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicBlacklist.Remove(ipOrCidr))
            {
                BuildIpNetworks(); // 重新构建网络列表
                _logger.Info("已从黑名单移除: {IpOrCidr}", ipOrCidr);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取当前黑名单列表
    /// </summary>
    public List<string> GetBlacklist()
    {
        lock (_dynamicListLock)
        {
            var allBlacklist = new List<string>(_options.IpControl.Blacklist);
            allBlacklist.AddRange(_dynamicBlacklist);
            return allBlacklist.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// 添加禁止访问的国家/地区
    /// </summary>
    public bool AddDenyCountry(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return false;

        country = country.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicDenyCountries.Add(country))
            {
                _logger.Info("已动态添加禁止访问的国家/地区: {Country}", country);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 移除禁止访问的国家/地区
    /// </summary>
    public bool RemoveDenyCountry(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return false;

        country = country.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicDenyCountries.Remove(country))
            {
                _logger.Info("已从禁止列表移除国家/地区: {Country}", country);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取禁止访问的国家/地区列表
    /// </summary>
    public List<string> GetDenyCountries()
    {
        lock (_dynamicListLock)
        {
            var allDenyCountries = new List<string>(_options.GeoControl.DenyCountries);
            allDenyCountries.AddRange(_dynamicDenyCountries);
            return allDenyCountries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// 添加禁止访问的省份
    /// </summary>
    public bool AddDenyRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return false;

        region = region.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicDenyRegions.Add(region))
            {
                _logger.Info("已动态添加禁止访问的省份: {Region}", region);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 移除禁止访问的省份
    /// </summary>
    public bool RemoveDenyRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return false;

        region = region.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicDenyRegions.Remove(region))
            {
                _logger.Info("已从禁止列表移除省份: {Region}", region);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取禁止访问的省份列表
    /// </summary>
    public List<string> GetDenyRegions()
    {
        lock (_dynamicListLock)
        {
            var allDenyRegions = new List<string>(_options.GeoControl.DenyRegions);
            allDenyRegions.AddRange(_dynamicDenyRegions);
            return allDenyRegions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// 添加允许访问的国家/地区
    /// </summary>
    public bool AddAllowCountry(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return false;

        country = country.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicAllowCountries.Add(country))
            {
                _logger.Info("已动态添加允许访问的国家/地区: {Country}", country);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 移除允许访问的国家/地区
    /// </summary>
    public bool RemoveAllowCountry(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return false;

        country = country.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicAllowCountries.Remove(country))
            {
                _logger.Info("已从允许列表移除国家/地区: {Country}", country);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取允许访问的国家/地区列表
    /// </summary>
    public List<string> GetAllowCountries()
    {
        lock (_dynamicListLock)
        {
            var allAllowCountries = new List<string>(_options.GeoControl.AllowCountries);
            allAllowCountries.AddRange(_dynamicAllowCountries);
            return allAllowCountries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// 添加允许访问的省份
    /// </summary>
    public bool AddAllowRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return false;

        region = region.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicAllowRegions.Add(region))
            {
                _logger.Info("已动态添加允许访问的省份: {Region}", region);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 移除允许访问的省份
    /// </summary>
    public bool RemoveAllowRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return false;

        region = region.Trim();

        lock (_dynamicListLock)
        {
            if (_dynamicAllowRegions.Remove(region))
            {
                _logger.Info("已从允许列表移除省份: {Region}", region);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取允许访问的省份列表
    /// </summary>
    public List<string> GetAllowRegions()
    {
        lock (_dynamicListLock)
        {
            var allAllowRegions = new List<string>(_options.GeoControl.AllowRegions);
            allAllowRegions.AddRange(_dynamicAllowRegions);
            return allAllowRegions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
