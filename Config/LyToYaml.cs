using System.Net;
using System.Text;
using NLog;

namespace LyWaf.Config;

/// <summary>
/// 将 Dictionary 转换为 YAML 格式
/// </summary>
public static class LyToYamlConverter
{
    /// <summary>
    /// 将字典转换为 YAML 字符串
    /// </summary>
    public static string DictToYaml(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        WriteYaml(sb, dict, 0);
        return sb.ToString();
    }

    private static void WriteYaml(StringBuilder sb, Dictionary<string, object> dict, int indent)
    {
        foreach (var kv in dict)
        {
            WriteYamlValue(sb, kv.Key, kv.Value, indent);
        }
    }

    private static void WriteYamlValue(StringBuilder sb, string key, object? value, int indent)
    {
        var prefix = new string(' ', indent * 2);

        // 先检查是否是列表类型（非字符串、非字典的 IEnumerable）
        if (value is System.Collections.IEnumerable enumerable && value is not string && value is not Dictionary<string, object>)
        {
            sb.AppendLine($"{prefix}{key}:");
            foreach (var item in enumerable)
            {
                if (item is Dictionary<string, object> itemDict)
                {
                    sb.Append($"{prefix}  - ");
                    var first = true;
                    foreach (var kv in itemDict)
                    {
                        if (first)
                        {
                            WriteInlineValue(sb, kv.Key, kv.Value);
                            sb.AppendLine();
                            first = false;
                        }
                        else
                        {
                            WriteYamlValue(sb, kv.Key, kv.Value, indent + 2);
                        }
                    }
                }
                else
                {
                    sb.AppendLine($"{prefix}  - {FormatValue(item)}");
                }
            }
            return;
        }

        switch (value)
        {
            case null:
                sb.AppendLine($"{prefix}{key}: ~");
                break;

            case bool boolVal:
                sb.AppendLine($"{prefix}{key}: {(boolVal ? "true" : "false")}");
                break;

            case int or long or float or double:
                sb.AppendLine($"{prefix}{key}: {value}");
                break;

            case string strVal:
                if (NeedsQuoting(strVal))
                {
                    sb.AppendLine($"{prefix}{key}: \"{EscapeString(strVal)}\"");
                }
                else
                {
                    sb.AppendLine($"{prefix}{key}: {strVal}");
                }
                break;

            case Dictionary<string, object> dictVal:
                sb.AppendLine($"{prefix}{key}:");
                foreach (var kv in dictVal)
                {
                    WriteYamlValue(sb, kv.Key, kv.Value, indent + 1);
                }
                break;

            default:
                sb.AppendLine($"{prefix}{key}: {value}");
                break;
        }
    }

    private static void WriteInlineValue(StringBuilder sb, string key, object? value)
    {
        sb.Append($"{key}: {FormatValue(value)}");
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "~",
            bool b => b ? "true" : "false",
            string s when NeedsQuoting(s) => $"\"{EscapeString(s)}\"",
            _ => value.ToString() ?? ""
        };
    }

    private static bool NeedsQuoting(string str)
    {
        if (string.IsNullOrEmpty(str))
            return true;

        if (str.Contains(':') || str.Contains('#') || str.Contains('"') ||
            str.Contains('\'') || str.Contains('\n') || str.Contains('\r') ||
            str.Contains('{') || str.Contains('}') || str.Contains('[') ||
            str.Contains(']') || str.Contains(',') || str.Contains('&') ||
            str.Contains('*') || str.Contains('!') || str.Contains('|') ||
            str.Contains('>') || str.Contains('%') || str.Contains('@'))
            return true;

        if (str.StartsWith(' ') || str.EndsWith(' ') ||
            str.StartsWith('-') || str.StartsWith('?'))
            return true;

        var lower = str.ToLower();
        if (lower == "true" || lower == "false" || lower == "null" ||
            lower == "yes" || lower == "no" || lower == "on" || lower == "off")
            return true;

        if (double.TryParse(str, out _))
            return true;

        return false;
    }

    private static string EscapeString(string str)
    {
        return str
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

/// <summary>
/// 配置转换上下文，用于在处理过程中共享状态
/// </summary>
public class LyConfigContext
{
    /// <summary>
    /// 全局 Clusters 配置
    /// </summary>
    public Dictionary<string, object> Clusters { get; } = new();
    
    /// <summary>
    /// Cluster 缓存：key 是 upstream 地址的组合，value 是 clusterId
    /// 用于复用相同 upstream 的 cluster
    /// </summary>
    public Dictionary<string, string> ClusterCache { get; } = new();
    
    /// <summary>
    /// 监听配置
    /// </summary>
    public List<object> Listens { get; } = new();
    
    /// <summary>
    /// 监听配置缓存：key 是 "host:port"，value 是 isHttps
    /// 用于去重和冲突检测
    /// </summary>
    private readonly Dictionary<string, bool> _listenCache = new();
    
    /// <summary>
    /// 添加监听配置（自动去重和冲突检测）
    /// </summary>
    /// <param name="host">监听地址</param>
    /// <param name="port">监听端口</param>
    /// <param name="isHttps">是否 HTTPS</param>
    /// <exception cref="LyConfigException">当相同端口存在 HTTP/HTTPS 冲突时抛出</exception>
    public void AddListen(string host, int port, bool isHttps)
    {
        var key = $"{host}:{port}";
        
        if (_listenCache.TryGetValue(key, out var existingIsHttps))
        {
            // 已存在相同的 host:port
            if (existingIsHttps == isHttps)
            {
                // 完全相同，忽略（去重）
                return;
            }
            else
            {
                // HTTP/HTTPS 冲突
                var existingProtocol = existingIsHttps ? "HTTPS" : "HTTP";
                var newProtocol = isHttps ? "HTTPS" : "HTTP";
                throw new LyConfigException(
                    $"监听配置冲突: {host}:{port} 已配置为 {existingProtocol}，无法再配置为 {newProtocol}");
            }
        }
        
        // 添加新的监听配置
        _listenCache[key] = isHttps;
        Listens.Add(new Dictionary<string, object>
        {
            ["Host"] = host,
            ["Port"] = port,
            ["IsHttps"] = isHttps
        });
    }
    
    /// <summary>
    /// 证书配置
    /// </summary>
    public List<object> Certs { get; } = new();
    
    /// <summary>
    /// 路由配置
    /// </summary>
    public Dictionary<string, object> Routes { get; } = new();
    
    /// <summary>
    /// 路由索引计数器
    /// </summary>
    public int RouteIndex { get; set; } = 1;
    
    /// <summary>
    /// Cluster 索引计数器
    /// </summary>
    public int ClusterIndex { get; set; } = 1;
    
    /// <summary>
    /// 是否有文件服务路由（需要确保 cluster1 存在）
    /// </summary>
    public bool HasFileServerRoute { get; set; } = false;
    
    /// <summary>
    /// SimpleRes 简单响应配置
    /// </summary>
    public Dictionary<string, object> SimpleResItems { get; } = new();
    
    /// <summary>
    /// SimpleRes 索引计数器
    /// </summary>
    public int SimpleResIndex { get; set; } = 1;
    
    /// <summary>
    /// FileServer 文件服务配置
    /// </summary>
    public Dictionary<string, object> FileServerItems { get; } = new();
    
    /// <summary>
    /// FileServer 索引计数器
    /// </summary>
    public int FileServerIndex { get; set; } = 1;
    
    /// <summary>
    /// 域名日志配置（站点级别的 log 指令）
    /// Key: 域名
    /// Value: 日志配置
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> LyLogs { get; } = new();
    
    /// <summary>
    /// 获取下一个路由 ID
    /// </summary>
    public string NextRouteId() => $"route{RouteIndex++}";
    
    /// <summary>
    /// 获取下一个 SimpleRes ID
    /// </summary>
    public string NextSimpleResId() => $"simpleres_{SimpleResIndex++}";
    
    /// <summary>
    /// 获取下一个 FileServer ID
    /// </summary>
    public string NextFileServerId() => $"fileserver_{FileServerIndex++}";
    
    /// <summary>
    /// 计算路由的 Order 值（全局唯一）
    /// 更具体的路径 Order 更小（优先级更高）
    /// </summary>
    public int NextRouteOrder(string path, bool hasHosts)
    {
        var baseOrder = 0;
        
        // 有 Hosts 的路由优先级更高
        if (!hasHosts)
        {
            baseOrder += 100000;
        }
        
        // 通配符路由优先级最低
        if (path.Contains("{**catch-all}") || path.Contains("{**file-all}") || path == "/{**catch-all}" || path == "/{**file-all}")
        {
            baseOrder += 10000;
        }
        else if (path.Contains("{**"))
        {
            baseOrder += 5000;
        }
        
        // 路径越短，优先级越低（更通用）
        baseOrder += (10 - Math.Min(10, path.Split('/').Length)) * 100;
        
        return baseOrder;
    }
    
    /// <summary>
    /// 获取下一个 Cluster ID
    /// </summary>
    public string NextClusterId() => $"cluster{ClusterIndex++}";
}

/// <summary>
/// LyConfig 到 appsettings 的映射转换器
/// 将 Caddy 风格的配置转换为 LyWaf 的 appsettings.yaml 格式
/// 
/// 格式参考: https://caddyserver.com/docs/caddyfile/concepts
/// </summary>
public static class LyToAppSettingsConverter
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 将 .ly 配置转换为 appsettings.yaml 格式
    /// </summary>
    public static string Convert(string lyContent, Dictionary<string, string>? variables = null)
    {
        var config = LyConfigParser.Parse(lyContent, variables);
        var appSettings = TransformToAppSettings(config);
        return LyToYamlConverter.DictToYaml(appSettings);
    }

    /// <summary>
    /// 将解析后的配置转换为 appsettings 格式
    /// Caddy 风格：以域名/地址为站点块，全局选项在顶部
    /// </summary>
    public static Dictionary<string, object> TransformToAppSettings(Dictionary<string, object> config)
    {
        var result = new Dictionary<string, object>();
        var wafInfos = new Dictionary<string, object>();
        var ctx = new LyConfigContext();

        foreach (var kv in config)
        {
            var key = kv.Key;
            var value = kv.Value;

            // 检查是否是站点块（地址格式）
            if (IsSiteAddress(key))
            {
                // 站点块 - 解析地址和内容
                ProcessSiteBlock(key, value, ctx);
            }
            else if (key.StartsWith("(") && key.EndsWith(")"))
            {
                // 代码片段 - 已在解析阶段处理
                continue;
            }
            else
            {
                // 其他顶级配置（全局选项或直接配置）
                ProcessGlobalOption(key, value, result, wafInfos, ctx);
            }
        }

        // 构建 WafInfos
        if (ctx.Listens.Count > 0)
        {
            wafInfos["Listens"] = ctx.Listens;
        }
        if (ctx.Certs.Count > 0)
        
        {
            wafInfos["Certs"] = ctx.Certs;
        }
        if (wafInfos.Count > 0)
        {
            result["WafInfos"] = wafInfos;
        }

        // 如果有文件服务路由，确保 cluster1 存在
        if (ctx.HasFileServerRoute && !ctx.Clusters.ContainsKey("cluster1"))
        {
            // 创建一个假的 cluster1，YARP 底层需要有效的 ClusterId
            ctx.Clusters["cluster1"] = new Dictionary<string, object>
            {
                ["Destinations"] = new Dictionary<string, object>
                {
                    ["dest1"] = new Dictionary<string, object>
                    {
                        ["Address"] = "http://example.com"
                    }
                }
            };
        }

        // 添加 cluster_unuse（用于代理等无需后端的场景）
        ctx.Clusters["cluster_unuse"] = new Dictionary<string, object>
        {
            ["Destinations"] = new Dictionary<string, object>
            {
                ["dest1"] = new Dictionary<string, object>
                {
                    ["Address"] = "http://0.0.0.0"
                }
            }
        };

        // 构建 ReverseProxy
        if (ctx.Routes.Count > 0 || ctx.Clusters.Count > 0)
        {
            var reverseProxy = new Dictionary<string, object>();
            if (ctx.Routes.Count > 0)
            {
                reverseProxy["Routes"] = ctx.Routes;
            }
            if (ctx.Clusters.Count > 0)
            {
                reverseProxy["Clusters"] = ctx.Clusters;
            }
            result["ReverseProxy"] = reverseProxy;
        }

        // 构建 FileServer（文件服务配置，按路由 ID 映射）
        if (ctx.FileServerItems.Count > 0)
        {
            result["FileServer"] = new Dictionary<string, object>
            {
                ["Items"] = ctx.FileServerItems
            };
        }

        // 构建 SimpleRes
        if (ctx.SimpleResItems.Count > 0)
        {
            result["SimpleRes"] = new Dictionary<string, object>
            {
                ["Items"] = ctx.SimpleResItems
            };
        }

        // 合并站点级别的日志配置到 LyLog
        if (ctx.LyLogs.Count > 0)
        {
            var domainLog = EnsureDict(result, "LyLog");
            domainLog["Enabled"] = true;
            
            var domains = EnsureDict(domainLog, "Domains");
            foreach (var (domain, logConfig) in ctx.LyLogs)
            {
                // 如果已存在该域名的配置，合并（站点级配置优先）
                if (domains.TryGetValue(domain, out var existing) && existing is Dictionary<string, object> existingDict)
                {
                    foreach (var kv in logConfig)
                    {
                        existingDict[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    domains[domain] = logConfig;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 判断是否是站点地址格式
    /// 支持: example.com, :8080, https://example.com, http://example.com, *.example.com
    /// 支持空格分隔的多地址: localhost:5003 localhost:5004
    /// </summary>
    private static bool IsSiteAddress(string key)
    {
        // 支持空格分隔的多地址，只要第一个是站点地址即可
        var firstAddr = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? key;
        return IsSingleSiteAddress(firstAddr);
    }

    /// <summary>
    /// 判断单个地址是否是站点地址格式
    /// </summary>
    private static bool IsSingleSiteAddress(string key)
    {
        // 端口格式 :port
        if (key.StartsWith(':') && int.TryParse(key[1..], out _))
            return true;

        // URL 格式
        if (key.StartsWith("http://") || key.StartsWith("https://"))
            return true;

        // 域名格式 (包含 . 或 *)
        if (key.Contains('.') || key.StartsWith('*'))
            return true;

        var val = key.Split(':');
        // localhost
        if (val[0].Equals("localhost", StringComparison.CurrentCultureIgnoreCase)) {
            if(val.Length == 1 || int.TryParse(val[1], out _)) {
                return true;
            }
            return false;
        }
        // IP 地址格式
        if (System.Net.IPAddress.TryParse(val[0], out _)) {
            if(val.Length == 1 || int.TryParse(val[1], out _)) {
                return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// 处理站点块
    /// 支持多路由配置：
    ///   - handle /path { reverse_proxy ... }
    ///   - route /path { reverse_proxy ... }
    ///   - reverse_proxy（默认路由）
    ///   - file_server（文件服务）
    /// 支持多地址配置：localhost:5003, localhost:5004 共享相同配置
    /// </summary>
    private static void ProcessSiteBlock(string address, object content, LyConfigContext ctx)
    {
        // 解析地址 - 可能包含多个域名/地址（空格分隔）
        var addresses = address.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hosts = new List<string>();
        var isHttps = false;
        
        foreach (var addr in addresses)
        {
            var parsed = ParseSiteAddress(addr);
            if (parsed.Host != null)
            {
                // Hosts 中包含端口信息：host:port
                if (parsed.Port > 0)
                {
                    hosts.Add($"{parsed.Host}:{parsed.Port}");
                }
                else
                {
                    hosts.Add(parsed.Host);
                }
            }
            if (parsed.IsHttps)
            {
                isHttps = true;
            }
            
            // 为每个地址创建监听配置（通过 AddListen 自动去重和冲突检测）
            if (parsed.Port > 0)
            {
                var listenHost = parsed.Host ?? "0.0.0.0";
                
                // localhost 转换为 127.0.0.1
                if (listenHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    listenHost = "127.0.0.1";
                } 
                else if (!IPAddress.TryParse(listenHost, out _)) 
                {
                    listenHost = "0.0.0.0";
                }
                
                // 使用 AddListen 添加（自动去重和冲突检测）
                ctx.AddListen(listenHost, parsed.Port, parsed.IsHttps || isHttps);
                
                // 如果没有 host，添加默认的 localhost 和 127.0.0.1 到 hosts
                if (parsed.Host == null)
                {
                    if (!hosts.Contains($"localhost:{parsed.Port}"))
                        hosts.Add($"localhost:{parsed.Port}");
                    if (!hosts.Contains($"127.0.0.1:{parsed.Port}"))
                        hosts.Add($"127.0.0.1:{parsed.Port}");
                }
            }
        }

        // 处理站点内容
        // 支持简化配置：localhost file_server 或 localhost reverse_proxy http://...
        if (content is string simpleContent)
        {
            var parts = simpleContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                var directive = parts[0].ToLower();
                if (directive == "file_server")
                {
                    // 简化的文件服务配置
                    ctx.HasFileServerRoute = true;
                    var fileServerId = BuildFileServerConfig(new Dictionary<string, object>(), "/", hosts, ctx);

                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = "/{**file-all}"
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }

                    ctx.Routes[fileServerId] = new Dictionary<string, object>
                    {
                        ["ClusterId"] = "cluster1",
                        ["Match"] = match,
                        ["Order"] = ctx.NextRouteOrder("/{**file-all}", hosts.Count > 0)
                    };
                }
                else if ((directive == "reverse_proxy" || directive == "proxy") && parts.Length > 1)
                {
                    // 简化的反向代理配置（proxy 是 reverse_proxy 的简写）
                    var upstreams = parts.Skip(1).Select(NormalizeUpstream).ToList();
                    var clusterId = GetOrCreateCluster(upstreams, new Dictionary<string, object>(), ctx);

                    var routeId = ctx.NextRouteId();
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = "/{**catch-all}"
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }

                    ctx.Routes[routeId] = new Dictionary<string, object>
                    {
                        ["ClusterId"] = clusterId,
                        ["Match"] = match,
                        ["Order"] = ctx.NextRouteOrder("/{**catch-all}", hosts.Count > 0)
                    };
                }
                else if (directive == "respond" && parts.Length > 1)
                {
                    // 简化的 respond 配置: respond "body"
                    var respondConfig = new Dictionary<string, object>
                    {
                        ["body"] = string.Join(" ", parts.Skip(1))
                    };
                    
                    // 使用 simpleres_xxx 作为路由 ID
                    var routeId = BuildSimpleResConfig(respondConfig, ctx);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = "/{**catch-all}"
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }

                    ctx.Routes[routeId] = new Dictionary<string, object>
                    {
                        ["ClusterId"] = "cluster1",
                        ["Match"] = match,
                        ["Order"] = ctx.NextRouteOrder("/{**catch-all}", hosts.Count > 0)
                    };
                    ctx.HasFileServerRoute = true; // 确保 cluster1 存在
                }
            }
            return;
        }

        if (content is Dictionary<string, object> siteContent)
        {
            // 收集 handle/route 块和默认配置
            var handleBlocks = new List<(string path, Dictionary<string, object> config)>();
            var defaultUpstreams = new List<string>();
            var defaultClusterConfig = new Dictionary<string, object>();
            var hasFileServer = false;
            var defaultFileServerConfig = new Dictionary<string, object>();
            var hasRespond = false;
            var defaultRespondConfig = new Dictionary<string, object>();

            foreach (var directive in siteContent)
            {
                var key = directive.Key.ToLower();
                
                // 处理 handle 或 route 块（带路径的子路由）
                if ((key == "handle" || key == "route") && directive.Value is Dictionary<string, object> handleConfig)
                {
                    // handle 块内的配置
                    ProcessHandleBlock(handleConfig, handleBlocks);
                }
                // 检查是否是路径格式的 key（如 /api/*）
                else if (key.StartsWith('/') && directive.Value is Dictionary<string, object> pathConfig)
                {
                    var path = directive.Key;
                    handleBlocks.Add((path, pathConfig));
                }
                else
                {
                    switch (key)
                    {
                        case "reverse_proxy":
                        case "proxy":  // proxy 是 reverse_proxy 的简写
                            // 解析 reverse_proxy 配置
                            ParseReverseProxyConfig(directive.Value, defaultUpstreams, defaultClusterConfig);
                            break;

                        case "file_server":
                            hasFileServer = true;
                            if (directive.Value is Dictionary<string, object> fsConfig)
                            {
                                defaultFileServerConfig = fsConfig;
                            }
                            break;

                        case "lb_policy":
                        case "load_balancing_policy":
                            defaultClusterConfig["LoadBalancingPolicy"] = directive.Value.ToString()!;
                            break;

                        case "abtest_id":
                        case "ab_test_id":
                        case "abtestid":
                            // A/B 测试 ID，用于 ABCookieTest 策略
                            var metaAbTest = EnsureDict(defaultClusterConfig, "Metadata");
                            metaAbTest["ABTestId"] = directive.Value.ToString()!;
                            break;

                        case "ab_cookie":
                        case "abcookie":
                        case "ab_test_cookie":
                            // A/B 测试 Cookie 名称，用于 ABTestWeighted 策略
                            var metaCookie = EnsureDict(defaultClusterConfig, "Metadata");
                            metaCookie["ABTestCookie"] = directive.Value.ToString()!;
                            break;

                        case "ab_cookie_expire":
                        case "abcookieexpire":
                        case "ab_cookie_expire_days":
                            // A/B 测试 Cookie 有效期（天）
                            var metaExpire = EnsureDict(defaultClusterConfig, "Metadata");
                            metaExpire["ABTestCookieExpireDays"] = directive.Value.ToString()!;
                            break;

                        case "health_check":
                            if (directive.Value is Dictionary<string, object> hcConfig)
                            {
                                defaultClusterConfig["HealthCheck"] = hcConfig;
                            }
                            break;

                        case "respond":
                            hasRespond = true;
                            if (directive.Value is Dictionary<string, object> respondConfig)
                            {
                                foreach (var kv in respondConfig)
                                {
                                    defaultRespondConfig[kv.Key] = kv.Value;
                                }
                            }
                            else if (directive.Value is string respondBody)
                            {
                                defaultRespondConfig["body"] = respondBody;
                            }
                            break;

                        case "status":
                            // respond 的状态码配置
                            defaultRespondConfig["status"] = directive.Value;
                            break;

                        case "content-type":
                        case "content_type":
                            // respond 的 Content-Type 配置
                            defaultRespondConfig["content-type"] = directive.Value;
                            break;

                        case "charset":
                            // respond 的编码配置
                            defaultRespondConfig["charset"] = directive.Value;
                            break;

                        case "show-req":
                        case "show_req":
                        case "showreq":
                            // respond 的显示请求头配置
                            defaultRespondConfig["show_req"] = directive.Value;
                            break;

                        case "log":
                            // 域名日志配置
                            ProcessSiteLogConfig(hosts, directive.Value, ctx);
                            break;

                        // file_server 的相关配置属性（非嵌套配置时这些是平级的）
                        case "root":
                        case "basepath":
                        case "base_path":
                        case "browse":
                        case "default":
                        case "index":
                        case "try_files":
                        case "tryfiles":
                        case "precompressed":
                        case "pre_compressed":
                        case "max_file_size":
                        case "maxfilesize":
                            // 将这些属性收集到 file_server 配置中
                            defaultFileServerConfig[directive.Key] = directive.Value;
                            break;
                    }
                }
            }

            // 处理 handle 块生成的路由
            foreach (var (path, config) in handleBlocks)
            {
                var upstreams = new List<string>();
                var clusterConfig = new Dictionary<string, object>(defaultClusterConfig);
                var blockHasFileServer = false;
                var blockFileServerConfig = new Dictionary<string, object>();

                var blockHasRespond = false;
                var blockRespondConfig = new Dictionary<string, object>();

                // 解析 handle 块内的配置
                foreach (var kv in config)
                {
                    switch (kv.Key.ToLower())
                    {
                        case "reverse_proxy":
                        case "proxy":  // proxy 是 reverse_proxy 的简写
                            // 解析 reverse_proxy 配置
                            ParseReverseProxyConfig(kv.Value, upstreams, clusterConfig);
                            break;
                        case "file_server":
                            blockHasFileServer = true;
                            if (kv.Value is Dictionary<string, object> fsConfig)
                            {
                                blockFileServerConfig = fsConfig;
                            }
                            break;
                        case "lb_policy":
                        case "load_balancing_policy":
                            clusterConfig["LoadBalancingPolicy"] = kv.Value.ToString()!;
                            break;
                        case "abtest_id":
                        case "ab_test_id":
                        case "abtestid":
                            var blockMetaAbTest = EnsureDict(clusterConfig, "Metadata");
                            blockMetaAbTest["ABTestId"] = kv.Value.ToString()!;
                            break;
                        case "ab_cookie":
                        case "abcookie":
                        case "ab_test_cookie":
                            var blockMetaCookie = EnsureDict(clusterConfig, "Metadata");
                            blockMetaCookie["ABTestCookie"] = kv.Value.ToString()!;
                            break;
                        case "ab_cookie_expire":
                        case "abcookieexpire":
                        case "ab_cookie_expire_days":
                            var blockMetaExpire = EnsureDict(clusterConfig, "Metadata");
                            blockMetaExpire["ABTestCookieExpireDays"] = kv.Value.ToString()!;
                            break;
                        case "respond":
                            blockHasRespond = true;
                            if (kv.Value is Dictionary<string, object> respondConfig)
                            {
                                foreach (var rkv in respondConfig)
                                {
                                    blockRespondConfig[rkv.Key] = rkv.Value;
                                }
                            }
                            else if (kv.Value is string respondBody)
                            {
                                blockRespondConfig["body"] = respondBody;
                            }
                            break;
                        case "status":
                            blockRespondConfig["status"] = kv.Value;
                            break;
                        case "content-type":
                        case "content_type":
                            blockRespondConfig["content-type"] = kv.Value;
                            break;
                        case "charset":
                            blockRespondConfig["charset"] = kv.Value;
                            break;
                        case "show-req":
                        case "show_req":
                        case "showreq":
                            blockRespondConfig["show_req"] = kv.Value;
                            break;
                    }
                }

                // 解析路径中的方法（如 /api/* @post）
                var (purePath, pathMethods) = ParsePathAndMethods(path);
                // 从配置块中解析方法（如 method = post）
                var configMethods = ParseMethodsFromConfig(config);
                // 合并方法（配置块的方法优先）
                var methods = configMethods ?? pathMethods;

                if (upstreams.Count > 0)
                {
                    // 获取或创建 cluster
                    var clusterId = GetOrCreateCluster(upstreams, clusterConfig, ctx);

                    // 创建路由
                    var routeId = ctx.NextRouteId();
                    var normalizedPath = NormalizePath(purePath);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = normalizedPath
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }
                    AddMethodsToMatch(match, methods);

                    var routeConfig = new Dictionary<string, object>
                    {
                        ["ClusterId"] = clusterId,
                        ["Match"] = match,
                        ["Order"] = ctx.NextRouteOrder(normalizedPath, hosts.Count > 0)
                    };

                    ctx.Routes[routeId] = routeConfig;
                }
                else if (blockHasFileServer)
                {
                    // 文件服务路由 - 使用 fileserver_xxx 作为路由 ID
                    ctx.HasFileServerRoute = true;
                    
                    // 将路径转换为 prefix（用于 FileServerItem）
                    var prefix = PathToFileServerPrefix(purePath);
                    var fileServerId = BuildFileServerConfig(blockFileServerConfig, prefix, hosts, ctx);

                    // 创建文件服务路由，将 * 替换为 {**file-all}
                    var matchPath = NormalizeFileServerPath(purePath);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = matchPath
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }
                    AddMethodsToMatch(match, methods);

                    // 使用 cluster1，如果不存在会在后面创建假的
                    var routeConfig = new Dictionary<string, object>
                    {
                        ["ClusterId"] = "cluster1",
                        ["Match"] = match,
                        ["Order"] = ctx.NextRouteOrder(matchPath, hosts.Count > 0)
                    };

                    ctx.Routes[fileServerId] = routeConfig;
                }
                else if (blockHasRespond)
                {
                    // respond 路由 - 使用 simpleres_xxx 作为路由 ID
                    var routeId = BuildSimpleResConfig(blockRespondConfig, ctx);
                    var normalizedPath = NormalizePath(purePath);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = normalizedPath
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }
                    AddMethodsToMatch(match, methods);

                    var routeConfig = new Dictionary<string, object>
                    {
                        ["ClusterId"] = "cluster1",
                        ["Match"] = match,
                        ["Order"] = ctx.NextRouteOrder(normalizedPath, hosts.Count > 0)
                    };

                    ctx.Routes[routeId] = routeConfig;
                    ctx.HasFileServerRoute = true; // 确保 cluster1 存在
                }
            }

            // 检测冲突：同一域名下同时配置了默认的 respond 和 reverse_proxy/file_server
            var conflictCount = (defaultUpstreams.Count > 0 ? 1 : 0) + (hasFileServer ? 1 : 0) + (hasRespond ? 1 : 0);
            if (conflictCount > 1)
            {
                var hostInfo = hosts.Count > 0 ? $"域名 {string.Join(", ", hosts)}" : "默认站点";
                throw new LyConfigException($"配置错误：{hostInfo} 同时配置了多个根路径处理器（respond/file_server/reverse_proxy），请将其中一些配置到具体路径下");
            }

            // 处理默认路由（没有指定路径的 reverse_proxy）
            if (defaultUpstreams.Count > 0)
            {
                // 获取或创建 cluster
                var clusterId = GetOrCreateCluster(defaultUpstreams, defaultClusterConfig, ctx);

                // 创建路由
                var routeId = ctx.NextRouteId();
                var match = new Dictionary<string, object>
                {
                    ["Path"] = "/{**catch-all}"
                };
                if (hosts.Count > 0)
                {
                    match["Hosts"] = hosts;
                }

                var routeConfig = new Dictionary<string, object>
                {
                    ["ClusterId"] = clusterId,
                    ["Match"] = match,
                    ["Order"] = ctx.NextRouteOrder("/{**catch-all}", hosts.Count > 0)
                };

                ctx.Routes[routeId] = routeConfig;
            }
            else if (hasRespond)
            {
                // respond 默认路由 - 使用 simpleres_xxx 作为路由 ID
                var routeId = BuildSimpleResConfig(defaultRespondConfig, ctx);
                var match = new Dictionary<string, object>
                {
                    ["Path"] = "/{**catch-all}"
                };
                if (hosts.Count > 0)
                {
                    match["Hosts"] = hosts;
                }

                var routeConfig = new Dictionary<string, object>
                {
                    ["ClusterId"] = "cluster1",
                    ["Match"] = match,
                    ["Order"] = ctx.NextRouteOrder("/{**catch-all}", hosts.Count > 0)
                };

                ctx.Routes[routeId] = routeConfig;
                ctx.HasFileServerRoute = true; // 确保 cluster1 存在
            }
            else if (hasFileServer)
            {
                // 默认文件服务 - 使用 fileserver_xxx 作为路由 ID
                ctx.HasFileServerRoute = true;
                var fileServerId = BuildFileServerConfig(defaultFileServerConfig, "/", hosts, ctx);

                // 创建文件服务路由
                var match = new Dictionary<string, object>
                {
                    ["Path"] = "/{**file-all}"
                };
                if (hosts.Count > 0)
                {
                    match["Hosts"] = hosts;
                }

                // 使用 cluster1，如果不存在会在后面创建假的
                var routeConfig = new Dictionary<string, object>
                {
                    ["ClusterId"] = "cluster1",
                    ["Match"] = match,
                    ["Order"] = ctx.NextRouteOrder("/{**file-all}", hosts.Count > 0)
                };

                ctx.Routes[fileServerId] = routeConfig;
            }
        }
    }

    /// <summary>
    /// 构建 respond 指令的 SimpleRes 配置
    /// 创建 SimpleRes 条目并返回路由 ID（格式: simpleres_xxx）
    /// 支持:
    ///   - respond "body" - 设置响应体
    ///   - status 201 - 设置状态码
    ///   - content-type text/plain - 设置 Content-Type
    ///   - charset utf-8 - 设置编码
    /// </summary>
    private static string BuildSimpleResConfig(Dictionary<string, object> config, LyConfigContext ctx)
    {
        // 生成唯一的 SimpleRes ID（同时作为路由 ID）
        var key = ctx.NextSimpleResId();
        
        // 构建 SimpleRes Item
        var item = new Dictionary<string, object>();

        // 获取响应体
        if (config.TryGetValue("body", out var body))
        {
            item["Body"] = body?.ToString() ?? "";
        }

        // 获取 Content-Type，默认 text/plain
        var contentType = "text/plain";
        if (config.TryGetValue("content-type", out var ct))
        {
            contentType = ct?.ToString() ?? "text/plain";
        }
        item["ContentType"] = contentType;

        // 获取状态码，默认 200
        var statusCode = 200;
        if (config.TryGetValue("status", out var status))
        {
            if (status is int intStatus)
            {
                statusCode = intStatus;
            }
            else if (int.TryParse(status?.ToString(), out var parsedStatus))
            {
                statusCode = parsedStatus;
            }
        }
        item["StatusCode"] = statusCode;

        // 获取编码，默认 utf-8
        var charset = "utf-8";
        if (config.TryGetValue("charset", out var cs))
        {
            charset = cs?.ToString() ?? "utf-8";
        }
        item["Charset"] = charset;

        // 获取是否显示请求头
        if (config.TryGetValue("show-req", out var showReq) || config.TryGetValue("show_req", out showReq) || config.TryGetValue("showreq", out showReq))
        {
            var showReqValue = showReq is bool b ? b : showReq?.ToString()?.ToLower() == "true";
            if (showReqValue)
            {
                item["ShowReq"] = true;
            }
        }

        // 添加到 SimpleRes Items
        ctx.SimpleResItems[key] = item;

        // 返回 key 作为路由 ID
        return key;
    }

    /// <summary>
    /// 构建 file_server 指令的 FileServer 配置
    /// 创建 FileServer 条目并返回路由 ID（格式: fileserver_xxx）
    /// </summary>
    private static string BuildFileServerConfig(
        Dictionary<string, object> config,
        string prefix,
        List<string> hosts,
        LyConfigContext ctx)
    {
        // 生成唯一的 FileServer ID（同时作为路由 ID）
        var fileServerId = ctx.NextFileServerId();
        
        // 构建 FileServer Item
        var item = new Dictionary<string, object>
        {
            ["Prefix"] = prefix
        };

        // 默认使用当前目录
        var basePath = Environment.CurrentDirectory;

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();
            switch (key)
            {
                case "root":
                case "basepath":
                case "base_path":
                    basePath = kv.Value?.ToString() ?? basePath;
                    break;
                case "browse":
                    item["Browse"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "default":
                case "index":
                    if (kv.Value is List<object> defaultList)
                    {
                        item["Default"] = defaultList.Select(x => x?.ToString() ?? "").ToHashSet();
                    }
                    else if (kv.Value is string defaultStr)
                    {
                        item["Default"] = defaultStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    }
                    break;
                case "try_files":
                case "tryfiles":
                    if (kv.Value is List<object> tryList)
                    {
                        item["TryFiles"] = tryList.Select(x => x?.ToString() ?? "").ToArray();
                    }
                    else if (kv.Value is string tryStr)
                    {
                        item["TryFiles"] = tryStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    }
                    break;
                case "precompressed":
                case "pre_compressed":
                    item["PreCompressed"] = kv.Value is bool pb ? pb : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "max_file_size":
                case "maxfilesize":
                    if (kv.Value is long lfs)
                    {
                        item["MaxFileSize"] = lfs;
                    }
                    else if (long.TryParse(kv.Value?.ToString(), out var parsedSize))
                    {
                        item["MaxFileSize"] = parsedSize;
                    }
                    break;
            }
        }

        // 设置 BasePath
        item["BasePath"] = basePath;

        // 添加到 FileServer Items
        ctx.FileServerItems[fileServerId] = item;

        // 返回 fileServerId 作为路由 ID
        return fileServerId;
    }

    /// <summary>
    /// 获取或创建 Cluster
    /// 如果相同 upstream 配置已存在，则复用；否则创建新的
    /// </summary>
    /// <param name="upstreams">上游地址列表，支持带权重格式 "http://host:port@weight"</param>
    /// <param name="clusterConfig">Cluster 配置</param>
    /// <param name="ctx">配置上下文</param>
    private static string GetOrCreateCluster(
        List<string> upstreams,
        Dictionary<string, object> clusterConfig,
        LyConfigContext ctx)
    {
        // 生成缓存 key：排序后的 upstream 地址 + 配置 hash
        var sortedUpstreams = upstreams.OrderBy(u => u).ToList();
        var cacheKey = string.Join("|", sortedUpstreams);
        
        // 添加 lb_policy 到缓存 key（不同策略需要不同 cluster）
        if (clusterConfig.TryGetValue("LoadBalancingPolicy", out var lbPolicy))
        {
            cacheKey += $"@lb={lbPolicy}";
        }
        
        // 添加 ABTestId 到缓存 key
        if (clusterConfig.TryGetValue("Metadata", out var metaObj) && metaObj is Dictionary<string, object> meta)
        {
            if (meta.TryGetValue("ABTestId", out var abTestId))
            {
                cacheKey += $"@abtest={abTestId}";
            }
        }

        // 检查缓存
        if (ctx.ClusterCache.TryGetValue(cacheKey, out var existingClusterId))
        {
            return existingClusterId;
        }

        // 创建新的 cluster
        var clusterId = ctx.NextClusterId();
        
        var destinations = new Dictionary<string, object>();
        var destIndex = 1;
        foreach (var upstream in upstreams)
        {
            // 解析 upstream，支持带权重格式: "http://host:port@weight" 或 "http://host:port weight=70"
            var (address, weight) = ParseUpstreamWithWeight(upstream);
            
            var destConfig = new Dictionary<string, object>
            {
                ["Address"] = address
            };
            
            // 如果有权重配置，添加到 Metadata
            if (weight > 0)
            {
                destConfig["Metadata"] = new Dictionary<string, object>
                {
                    ["Weight"] = weight.ToString()
                };
            }
            
            destinations[$"dest{destIndex++}"] = destConfig;
        }
        
        var newClusterConfig = new Dictionary<string, object>(clusterConfig)
        {
            ["Destinations"] = destinations
        };
        
        ctx.Clusters[clusterId] = newClusterConfig;
        ctx.ClusterCache[cacheKey] = clusterId;

        return clusterId;
    }
    
    /// <summary>
    /// 解析带权重的 upstream 地址
    /// 支持格式：
    ///   - "http://host:port" -> (address, 0)
    ///   - "http://host:port@70" -> (address, 70)
    ///   - "http://host:port weight=70" -> (address, 70)
    /// </summary>
    private static (string address, int weight) ParseUpstreamWithWeight(string upstream)
    {
        var weight = 0;
        var address = upstream.Trim();
        
        // 检查 @ 格式: "http://host:port@70"
        var atIndex = address.LastIndexOf('@');
        if (atIndex > 0 && atIndex < address.Length - 1)
        {
            var weightPart = address[(atIndex + 1)..];
            if (int.TryParse(weightPart, out var w))
            {
                weight = w;
                address = address[..atIndex];
            }
        }
        
        // 检查 weight= 格式: "http://host:port weight=70"
        var weightIdx = address.IndexOf(" weight=", StringComparison.OrdinalIgnoreCase);
        if (weightIdx > 0)
        {
            var weightPart = address[(weightIdx + 8)..].Trim();
            if (int.TryParse(weightPart, out var w))
            {
                weight = w;
                address = address[..weightIdx].Trim();
            }
        }
        
        return (address, weight);
    }

    /// <summary>
    /// 处理 handle 块
    /// </summary>
    private static void ProcessHandleBlock(Dictionary<string, object> config, List<(string path, Dictionary<string, object> config)> handleBlocks)
    {
        // handle 块可能有 path 属性，或者直接是配置
        string path = "/{**catch-all}";
        var innerConfig = new Dictionary<string, object>();

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();
            if (key == "path" || key == "path_prefix")
            {
                path = kv.Value.ToString()!;
            }
            else if (key.StartsWith('/'))
            {
                // 嵌套的路径配置
                if (kv.Value is Dictionary<string, object> nestedConfig)
                {
                    handleBlocks.Add((kv.Key, nestedConfig));
                }
            }
            else
            {
                innerConfig[kv.Key] = kv.Value;
            }
        }

        if (innerConfig.Count > 0)
        {
            handleBlocks.Add((path, innerConfig));
        }
    }

    /// <summary>
    /// 解析路径和 HTTP 方法
    /// 支持格式:
    /// - /api/* @post -> 路径 /api/*，方法 POST
    /// - /api/* @get,post -> 路径 /api/*，方法 GET,POST
    /// - /api/* @GET @POST -> 路径 /api/*，方法 GET,POST
    /// </summary>
    private static (string path, List<string>? methods) ParsePathAndMethods(string input)
    {
        var methods = new List<string>();
        var path = input.Trim();

        // 查找 @ 符号来提取方法
        var atIndex = path.IndexOf(" @", StringComparison.Ordinal);
        if (atIndex > 0)
        {
            var methodPart = path[(atIndex + 1)..].Trim();
            path = path[..atIndex].Trim();

            // 解析方法（可能有多个 @method 或 @method1,method2）
            var tokens = methodPart.Split(new[] { ' ', '@' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                // 处理逗号分隔的方法
                var methodNames = token.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var m in methodNames)
                {
                    var method = m.Trim().ToUpper();
                    if (IsValidHttpMethod(method) && !methods.Contains(method))
                    {
                        methods.Add(method);
                    }
                }
            }
        }

        return (path, methods.Count > 0 ? methods : null);
    }

    /// <summary>
    /// 从配置块中解析 HTTP 方法
    /// 支持格式:
    /// - method = post
    /// - method = "post,get"
    /// - methods = ["GET", "POST"]
    /// </summary>
    private static List<string>? ParseMethodsFromConfig(Dictionary<string, object> config)
    {
        var methods = new List<string>();

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();
            if (key == "method" || key == "methods")
            {
                if (kv.Value is string strValue)
                {
                    // 处理 "post" 或 "post,get"
                    var methodNames = strValue.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var m in methodNames)
                    {
                        var method = m.Trim().ToUpper();
                        if (IsValidHttpMethod(method) && !methods.Contains(method))
                        {
                            methods.Add(method);
                        }
                    }
                }
                else if (kv.Value is List<object> listValue)
                {
                    // 处理 ["GET", "POST"]
                    foreach (var item in listValue)
                    {
                        var method = item.ToString()?.Trim().ToUpper();
                        if (method != null && IsValidHttpMethod(method) && !methods.Contains(method))
                        {
                            methods.Add(method);
                        }
                    }
                }
            }
        }

        return methods.Count > 0 ? methods : null;
    }

    /// <summary>
    /// 检查是否是有效的 HTTP 方法
    /// </summary>
    private static bool IsValidHttpMethod(string method)
    {
        return method switch
        {
            "GET" => true,
            "POST" => true,
            "PUT" => true,
            "DELETE" => true,
            "PATCH" => true,
            "HEAD" => true,
            "OPTIONS" => true,
            "TRACE" => true,
            "CONNECT" => true,
            _ => false
        };
    }

    /// <summary>
    /// 将方法列表添加到 Match 配置
    /// </summary>
    private static void AddMethodsToMatch(Dictionary<string, object> match, List<string>? methods)
    {
        if (methods != null && methods.Count > 0)
        {
            match["Methods"] = methods;
        }
    }

    /// <summary>
    /// 规范化路径格式
    /// </summary>
    private static string NormalizePath(string path)
    {
        // 先解析出纯路径（去除方法部分）
        var (purePath, _) = ParsePathAndMethods(path);
        path = purePath;

        // 确保路径以 / 开头
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // 处理通配符
        // /api/* -> /api/{**remainder}
        // /api/** -> /api/{**remainder}
        if (path.EndsWith("/*") || path.EndsWith("/**"))
        {
            var basePath = path.TrimEnd('*', '/');
            return $"{basePath}/{{**remainder}}";
        }

        return path;
    }

    /// <summary>
    /// 将路径转换为文件服务的 Match.Path 格式
    /// 将 * 替换为 {**file-all}
    /// 例如: /static/* -> /static/{**file-all}
    /// 例如: /show/*(.png|.jpg) -> /show/{**file-all}
    /// </summary>
    private static string NormalizeFileServerPath(string path)
    {
        // 确保路径以 / 开头
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        // 移除括号内的扩展名过滤（如 (.png|.jpg)），YARP 不支持这种模式
        // 过滤逻辑由 FileService 通过正则匹配处理
        var parenIndex = path.IndexOf('(');
        if (parenIndex > 0)
        {
            path = path[..parenIndex];
        }

        // 处理通配符：将末尾的 /* 或 /** 替换为 /{**file-all}
        if (path.EndsWith("/*") || path.EndsWith("/**") || path.EndsWith("*"))
        {
            var basePath = path.TrimEnd('*', '/');
            return $"{basePath}/{{**file-all}}";
        }

        // 如果路径中没有通配符，追加 {**file-all}
        if (!path.Contains('*'))
        {
            return path.TrimEnd('/') + "/{**file-all}";
        }

        // 处理路径中间的 * 
        return path.Replace("**", "{**file-all}").Replace("*", "{**file-all}");
    }

    /// <summary>
    /// 将路径转换为 FileServer 的 Prefix
    /// 简单通配符使用前缀匹配，带括号的扩展名过滤才使用正则表达式
    /// 例如: /static/* -> /static/（前缀匹配）
    /// 例如: /show/*(.png|.jpg) -> ^/show/.*(.png|.jpg)$（正则匹配）
    /// </summary>
    private static string PathToFileServerPrefix(string path)
    {
        // 确保路径以 / 开头
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (path.Contains('(') || path.Contains('['))
        {
            // 将 * 替换成 .*，但跳过 )* ]* }* 这些正则量词
            var reg = ReplaceWildcardToRegex(path);
            return $"^{reg}$";
        }

        // 简单通配符：只需要前缀匹配
        // /static/* -> /static/
        // /static/** -> /static/
        // /static -> /static/
        var prefix = path.TrimEnd('*', '/');
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }
        return prefix;
    }

    /// <summary>
    /// 将路径中的通配符 * 替换为正则表达式 .*
    /// 但跳过 )* ]* }* 这些正则量词（它们的 * 是重复零次或多次的含义）
    /// </summary>
    private static string ReplaceWildcardToRegex(string path)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '*')
            {
                // 检查前一个字符是否是 ) ] }，如果是则保留 * 作为正则量词
                if (i > 0)
                {
                    var prev = path[i - 1];
                    if (prev == ')' || prev == ']' || prev == '}')
                    {
                        sb.Append(c);
                        continue;
                    }
                }
                // 替换为 .*
                sb.Append(".*");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析站点地址
    /// </summary>
    private static (string? Host, int Port, bool IsHttps) ParseSiteAddress(string address)
    {
        string? host = null;
        int port = 0;
        bool isHttps = false;

        // 移除协议前缀
        if (address.StartsWith("https://"))
        {
            address = address[8..];
            isHttps = true;
            port = 443;
        }
        else if (address.StartsWith("http://"))
        {
            address = address[7..];
            port = 80;
        }

        // 解析 host:port
        if (address.StartsWith(':'))
        {
            // 仅端口
            if (int.TryParse(address[1..], out var p))
            {
                port = p;
                if (port == 443) isHttps = true;
            }
        }
        else if (address.Contains(':'))
        {
            var parts = address.Split(':');
            host = parts[0];
            if (int.TryParse(parts[1], out var p))
            {
                port = p;
                if (port == 443) isHttps = true;
            }
        }
        else
        {
            host = address;
        }

        return (host, port, isHttps);
    }

    /// <summary>
    /// 处理 ControlListen 配置
    /// 支持格式：
    /// 1. URL 字符串：control_listen = "http://127.0.0.1:7030"
    /// 2. 地址字符串：control_listen = "127.0.0.1:7030"
    /// 3. 仅端口：    control_listen = ":7030" 或 control_listen = "7030"
    /// 4. 对象格式：  control_listen { Host = "127.0.0.1"; Port = 7030 }
    /// </summary>
    private static void ProcessControlListenConfig(object value, Dictionary<string, object> wafInfos)
    {
        var controlListen = new Dictionary<string, object>();

        if (value is string strValue)
        {
            // 纯数字 → 仅端口
            if (int.TryParse(strValue, out var portOnly))
            {
                controlListen["Host"] = "127.0.0.1";
                controlListen["Port"] = portOnly;
                controlListen["IsHttps"] = false;
            }
            else
            {
                // 利用已有的 ParseSiteAddress 解析
                var (host, port, isHttps) = ParseSiteAddress(strValue);
                controlListen["Host"] = host ?? "127.0.0.1";
                controlListen["Port"] = port > 0 ? port : 7030;
                controlListen["IsHttps"] = isHttps;
            }
        }
        else if (value is Dictionary<string, object> dict)
        {
            // 对象格式：control_listen { Host = "127.0.0.1"; Port = 7030; IsHttps = false }
            foreach (var kv in dict)
            {
                var lk = kv.Key.ToLower();
                switch (lk)
                {
                    case "host":
                        controlListen["Host"] = kv.Value.ToString()!;
                        break;
                    case "port":
                        controlListen["Port"] = int.Parse(kv.Value.ToString()!);
                        break;
                    case "ishttps":
                    case "is_https":
                    case "https":
                        controlListen["IsHttps"] = kv.Value is true
                            || string.Equals(kv.Value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
            // 填充默认值
            if (!controlListen.ContainsKey("Host")) controlListen["Host"] = "127.0.0.1";
            if (!controlListen.ContainsKey("Port")) controlListen["Port"] = 7030;
            if (!controlListen.ContainsKey("IsHttps")) controlListen["IsHttps"] = false;
        }

        if (controlListen.Count > 0)
        {
            wafInfos["ControlListen"] = controlListen;
        }
    }

    /// <summary>
    /// 处理站点级别的 log 配置
    /// 支持格式：
    /// 1. 简单格式：log = "logs/example.com"  （指定输出目录）
    /// 2. 布尔格式：log = true  （启用，使用默认配置）
    /// 3. 完整格式：
    ///    log {
    ///        output = "logs/example.com"
    ///        level = "Debug"
    ///        format = "Json"
    ///        also_log_to_global = true
    ///        exclude_paths = ["/health", "/metrics"]
    ///    }
    /// </summary>
    private static void ProcessSiteLogConfig(List<string> hosts, object value, LyConfigContext ctx)
    {
        var logConfig = new Dictionary<string, object>
        {
            ["Enabled"] = true
        };

        switch (value)
        {
            case bool b:
                // 布尔格式：log = true
                logConfig["Enabled"] = b;
                break;

            case string s:
                // 简单字符串格式：log = "logs/example.com"
                logConfig["Output"] = s;
                break;

            case Dictionary<string, object> dict:
                // 完整配置格式
                logConfig = ParseLyLogConfig(dict);
                break;
        }

        // 为每个域名添加日志配置
        foreach (var host in hosts)
        {
            // 提取纯域名（去掉端口）
            var domain = host.Contains(':') ? host.Split(':')[0] : host;
            if (!string.IsNullOrEmpty(domain) && domain != "*")
            {
                ctx.LyLogs[domain] = logConfig;
            }
        }
    }

    /// <summary>
    /// 解析 reverse_proxy/proxy 配置
    /// 支持格式：
    /// 1. 简单格式：proxy http://127.0.0.1:8080
    /// 2. 多上游：proxy http://127.0.0.1:8080 http://127.0.0.1:8081
    /// 3. 带配置格式：
    ///    proxy {
    ///        to = "http://127.0.0.1:8080"
    ///        lb_policy = "RoundRobin"
    ///    }
    /// </summary>
    private static void ParseReverseProxyConfig(
        object value,
        List<string> upstreams,
        Dictionary<string, object> clusterConfig)
    {
        switch (value)
        {
            case string s:
                // 简单字符串格式：可能是空格分隔的多个上游
                foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    upstreams.Add(NormalizeUpstream(part));
                }
                break;

            case List<object> list:
                // 数组格式
                foreach (var item in list)
                {
                    upstreams.Add(NormalizeUpstream(item?.ToString() ?? ""));
                }
                break;

            case Dictionary<string, object> dict:
                // 完整配置格式
                foreach (var kv in dict)
                {
                    var key = kv.Key.ToLower();
                    switch (key)
                    {
                        case "to":
                        case "upstream":
                        case "upstreams":
                        case "address":
                        case "addresses":
                            // 上游地址
                            upstreams.AddRange(ParseUpstreams(kv.Value!));
                            break;

                        case "lb_policy":
                        case "load_balancing_policy":
                            clusterConfig["LoadBalancingPolicy"] = kv.Value?.ToString() ?? "RoundRobin";
                            break;

                        case "abtest_id":
                        case "ab_test_id":
                        case "abtestid":
                            // A/B 测试 ID，用于 ABCookieTest 策略
                            var metadata = EnsureDict(clusterConfig, "Metadata");
                            metadata["ABTestId"] = kv.Value?.ToString() ?? "";
                            break;

                        case "ab_cookie":
                        case "abcookie":
                        case "ab_test_cookie":
                            // A/B 测试 Cookie 名称，用于 ABTestWeighted 策略
                            var metaCookie = EnsureDict(clusterConfig, "Metadata");
                            metaCookie["ABTestCookie"] = kv.Value?.ToString() ?? "ab_variant";
                            break;

                        case "ab_cookie_expire":
                        case "abcookieexpire":
                        case "ab_cookie_expire_days":
                            // A/B 测试 Cookie 有效期（天）
                            var metaExpire = EnsureDict(clusterConfig, "Metadata");
                            metaExpire["ABTestCookieExpireDays"] = kv.Value?.ToString() ?? "30";
                            break;

                        case "health_check":
                            if (kv.Value is Dictionary<string, object> hcConfig)
                            {
                                clusterConfig["HealthCheck"] = hcConfig;
                            }
                            break;

                        case "timeout":
                        case "request_timeout":
                            // 请求超时配置
                            var httpClientTimeout = EnsureDict(clusterConfig, "HttpClient");
                            httpClientTimeout["RequestTimeout"] = kv.Value?.ToString() ?? "60";
                            break;

                        case "max_connections":
                        case "max_connections_per_server":
                            var httpClientConn = EnsureDict(clusterConfig, "HttpClient");
                            if (int.TryParse(kv.Value?.ToString(), out var maxConn))
                            {
                                httpClientConn["MaxConnectionsPerServer"] = maxConn;
                            }
                            break;

                        default:
                            // 尝试作为上游地址解析
                            if (kv.Value is Dictionary<string, object> destConfig)
                            {
                                if (destConfig.TryGetValue("Address", out var addr) ||
                                    destConfig.TryGetValue("address", out addr))
                                {
                                    upstreams.Add(NormalizeUpstream(addr?.ToString() ?? ""));
                                }
                            }
                            break;
                    }
                }
                break;
        }
    }

    private static Dictionary<string, object> EnsureDict(Dictionary<string, object> parent, string key)
    {
        if (!parent.TryGetValue(key, out var value) || value is not Dictionary<string, object> dict)
        {
            dict = new Dictionary<string, object>();
            parent[key] = dict;
        }
        return (Dictionary<string, object>)dict;
    }

    /// <summary>
    /// 解析上游服务器列表
    /// </summary>
    private static List<string> ParseUpstreams(object value)
    {
        var upstreams = new List<string>();

        switch (value)
        {
            case string s:
                // 可能是空格分隔的多个上游
                foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    upstreams.Add(NormalizeUpstream(part));
                }
                break;

            case List<object> list:
                foreach (var item in list)
                {
                    upstreams.Add(NormalizeUpstream(item?.ToString() ?? ""));
                }
                break;

            case Dictionary<string, object> dict:
                // 带配置的上游
                foreach (var kv in dict)
                {
                    if (kv.Value is Dictionary<string, object> destConfig)
                    {
                        if (destConfig.TryGetValue("Address", out var addr) ||
                            destConfig.TryGetValue("address", out addr))
                        {
                            upstreams.Add(NormalizeUpstream(addr?.ToString() ?? ""));
                        }
                    }
                    else
                    {
                        upstreams.Add(NormalizeUpstream(kv.Value?.ToString() ?? ""));
                    }
                }
                break;
        }

        return upstreams;
    }

    /// <summary>
    /// 规范化上游地址
    /// </summary>
    private static string NormalizeUpstream(string upstream)
    {
        upstream = upstream.Trim();
        if (!upstream.StartsWith("http://") && !upstream.StartsWith("https://"))
        {
            upstream = "http://" + upstream;
        }
        return upstream;
    }

    /// <summary>
    /// 处理全局选项
    /// </summary>
    private static void ProcessGlobalOption(
        string key,
        object value,
        Dictionary<string, object> result,
        Dictionary<string, object> wafInfos,
        LyConfigContext ctx)
    {
        var lowerKey = key.ToLower();

        switch (lowerKey)
        {
            case "email":
                // ACME 邮箱
                EnsureDict(result, "Acme")["Email"] = value.ToString()!;
                break;

            case "acme_staging":
            case "staging":
                EnsureDict(result, "Acme")["UseStaging"] = value;
                break;

            case "auto_https":
                // 自动 HTTPS 配置
                break;

            case "http_port":
                ctx.AddListen("0.0.0.0", int.Parse(value.ToString()!), false);
                break;

            case "https_port":
                ctx.AddListen("0.0.0.0", int.Parse(value.ToString()!), true);
                break;

            case "control_listen":
            case "controllisten":
            case "control":
                // 控制面板监听地址
                // 支持格式：
                //   control_listen = "http://127.0.0.1:7030"
                //   control_listen = "127.0.0.1:7030"
                //   control_listen = ":7030"
                //   control_listen { Host = "127.0.0.1"; Port = 7030 }
                ProcessControlListenConfig(value, wafInfos);
                break;

            case "debug":
                // 调试模式
                EnsureDict(result, "Logging")["Level"] = "Debug";
                break;

            case "allowedhosts":
            case "allowed_hosts":
                // AllowedHosts 配置
                result["AllowedHosts"] = value.ToString()!;
                break;

            case "customdns":
            case "custom_dns":
            case "dns":
                // 自定义 DNS 配置
                ProcessCustomDnsConfig(value, result);
                break;

            case "proxyserver":
            case "proxy_server":
            case "forward_proxy":
                // 正向代理服务配置
                ProcessProxyServerConfig(value, result, ctx);
                break;

            case "streamserver":
            case "stream_server":
            case "stream":
                // TCP 流代理配置
                ProcessStreamServerConfig(value, result);
                break;

            case "certs":
                // 证书配置
                // 支持格式：
                // Certs { PemFile = "xxx"; KeyFile = "xxx" }  - 默认证书
                // Certs { example.com { PemFile = "xxx"; KeyFile = "xxx" } }  - 域名特定证书
                ProcessCertsConfig(value, ctx.Certs);
                break;

            case "plugins":
                // 插件配置
                ProcessPluginsConfig(value, result);
                break;

            case "abtest":
            case "ab_test":
            case "abtesting":
            case "ab_testing":
                // A/B 测试配置
                ProcessABTestConfig(value, result);
                break;

            case "domainlog":
            case "domain_log":
            case "logging":
            case "lylog":
            case "ly_log":
                // 域名日志配置
                ProcessLyLogConfig(value, result);
                break;

            case "errortemplate":
            case "error_template":
            case "errortemplates":
            case "error_templates":
            case "error_pages":
            case "errorpages":
                // 错误模板配置
                ProcessErrorTemplateConfig(value, result);
                break;

            case "ccrules":
            case "cc_rules":
            case "ccprotection":
            case "cc_protection":
                // CC 防护规则配置 → WafInfos.CcRules
                ProcessCcRulesConfig(value, wafInfos);
                break;

            default:
                // 其他配置直接映射（首字母大写）
                var normalizedKey = char.ToUpper(key[0]) + key[1..];
                result[normalizedKey] = value;
                break;
        }
    }

    /// <summary>
    /// 处理正向代理服务配置
    /// 支持格式：
    /// ProxyServer {
    ///     Enabled = true
    ///     Username = "user"
    ///     Password = "pass"
    ///     ConnectTimeout = 30
    ///     DataTimeout = 300
    ///     AllowedHosts = ["*.example.com"]
    ///     BlockedHosts = ["*.blocked.com"]
    ///     Ports {
    ///         8080 { EnableHttp = true; EnableHttps = true; EnableSocks5 = false }
    ///         1080 { EnableSocks5 = true; RequireAuth = true }
    ///     }
    /// }
    /// </summary>
    private static void ProcessProxyServerConfig(object value, Dictionary<string, object> result, LyConfigContext ctx)
    {
        if (value is not Dictionary<string, object> proxyConfig)
            return;

        var proxyServer = EnsureDict(result, "ProxyServer");
        var ports = new Dictionary<string, object>();

        foreach (var kv in proxyConfig)
        {
            var key = kv.Key.ToLower();

            switch (key)
            {
                case "enabled":
                    proxyServer["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "username":
                case "user":
                    proxyServer["Username"] = kv.Value?.ToString() ?? "";
                    break;
                case "password":
                case "pass":
                    proxyServer["Password"] = kv.Value?.ToString() ?? "";
                    break;
                case "connecttimeout":
                case "connect_timeout":
                    if (int.TryParse(kv.Value?.ToString(), out var ct))
                    {
                        proxyServer["ConnectTimeout"] = ct;
                    }
                    break;
                case "datatimeout":
                case "data_timeout":
                    if (int.TryParse(kv.Value?.ToString(), out var dt))
                    {
                        proxyServer["DataTimeout"] = dt;
                    }
                    break;
                case "allowedhosts":
                case "allowed_hosts":
                case "whitelist":
                    proxyServer["AllowedHosts"] = ParseStringList(kv.Value);
                    break;
                case "blockedhosts":
                case "blocked_hosts":
                case "blacklist":
                    proxyServer["BlockedHosts"] = ParseStringList(kv.Value);
                    break;
                case "ports":
                    if (kv.Value is Dictionary<string, object> portsConfig)
                    {
                        foreach (var portKv in portsConfig)
                        {
                            // 支持纯端口号 (8080) 和 host:port 格式 (127.0.0.1:8080)
                            if (IsValidPortKey(portKv.Key))
                            {
                                var portConfig = ParsePortConfig(portKv.Value);
                                ports[portKv.Key] = portConfig;
                                // HTTP/HTTPS/SOCKS5 代理都使用独立的 TCP 监听，不需要添加到 YARP 监听配置
                            }
                        }
                    }
                    break;
                case "default":
                    if (kv.Value is Dictionary<string, object> defaultConfig)
                    {
                        proxyServer["Default"] = ParsePortConfig(defaultConfig);
                    }
                    break;
                default:
                    // 检查是否是端口号或 host:port 格式（直接在顶层配置端口）
                    if (IsValidPortKey(kv.Key))
                    {
                        var portConfig = ParsePortConfig(kv.Value);
                        ports[kv.Key] = portConfig;
                        // HTTP/HTTPS/SOCKS5 代理都使用独立的 TCP 监听，不需要添加到 YARP 监听配置
                    }
                    break;
            }
        }

        if (ports.Count > 0)
        {
            proxyServer["Ports"] = ports;
            // 如果有端口配置但没有显式设置 Enabled，默认启用
            if (!proxyServer.ContainsKey("Enabled"))
            {
                proxyServer["Enabled"] = true;
            }
            // HTTP/HTTPS/SOCKS5 代理使用独立的 TCP 监听，不需要添加 YARP 路由
        }
    }

    /// <summary>
    /// 处理 TCP 流代理配置
    /// 支持格式：
    /// StreamServer {
    ///     Enabled = true
    ///     ConnectTimeout = 30
    ///     DataTimeout = 300
    ///     3306 {
    ///         Upstreams = ["192.168.1.100:3306", "192.168.1.101:3306"]
    ///         Policy = "RoundRobin"
    ///     }
    ///     6379 {
    ///         Upstreams = ["redis.example.com:6379"]
    ///     }
    /// }
    /// 
    /// 或简写格式：
    /// StreamServer {
    ///     3306 = "192.168.1.100:3306"
    ///     6379 = ["redis1:6379", "redis2:6379"]
    /// }
    /// </summary>
    private static void ProcessStreamServerConfig(object value, Dictionary<string, object> result)
    {
        if (value is not Dictionary<string, object> streamConfig)
            return;

        var streamServer = EnsureDict(result, "StreamServer");
        var streams = new Dictionary<string, object>();

        foreach (var kv in streamConfig)
        {
            var key = kv.Key.ToLower();
            switch (key)
            {
                case "enabled":
                    streamServer["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "connecttimeout":
                case "connect_timeout":
                    if (int.TryParse(kv.Value?.ToString(), out var ct))
                    {
                        streamServer["ConnectTimeout"] = ct;
                    }
                    break;
                case "datatimeout":
                case "data_timeout":
                    if (int.TryParse(kv.Value?.ToString(), out var dt))
                    {
                        streamServer["DataTimeout"] = dt;
                    }
                    break;
                case "healthcheckinterval":
                case "health_check_interval":
                case "healthinterval":
                    if (int.TryParse(kv.Value?.ToString(), out var hci))
                    {
                        streamServer["HealthCheckInterval"] = hci;
                    }
                    break;
                case "healthchecktimeout":
                case "health_check_timeout":
                case "healthtimeout":
                    if (int.TryParse(kv.Value?.ToString(), out var hct))
                    {
                        streamServer["HealthCheckTimeout"] = hct;
                    }
                    break;
                case "unhealthythreshold":
                case "unhealthy_threshold":
                    if (int.TryParse(kv.Value?.ToString(), out var ut))
                    {
                        streamServer["UnhealthyThreshold"] = ut;
                    }
                    break;
                case "healthythreshold":
                case "healthy_threshold":
                    if (int.TryParse(kv.Value?.ToString(), out var ht))
                    {
                        streamServer["HealthyThreshold"] = ht;
                    }
                    break;
                case "streams":
                    // 嵌套的 Streams { ... } 块
                    if (kv.Value is Dictionary<string, object> streamsConfig)
                    {
                        foreach (var streamKv in streamsConfig)
                        {
                            if (IsValidPortKey(streamKv.Key))
                            {
                                var streamConf = ParseStreamConfig(streamKv.Value);
                                streams[streamKv.Key] = streamConf;
                            }
                        }
                    }
                    break;
                default:
                    // 检查是否是端口号或 host:port 格式（直接在顶层配置）
                    if (IsValidPortKey(kv.Key))
                    {
                        var streamConf = ParseStreamConfig(kv.Value);
                        streams[kv.Key] = streamConf;
                    }
                    break;
            }
        }

        if (streams.Count > 0)
        {
            streamServer["Streams"] = streams;
            // 如果有配置但没有显式设置 Enabled，默认启用
            if (!streamServer.ContainsKey("Enabled"))
            {
                streamServer["Enabled"] = true;
            }
        }
    }

    /// <summary>
    /// 处理插件配置
    /// 支持格式：
    /// Plugins {
    ///     Enabled = true
    ///     PluginDirectory = "plugins"
    ///     DataDirectory = "plugin_data"
    ///     EnableHotReload = false
    ///     DisabledPlugins = ["plugin-id-1", "plugin-id-2"]
    ///     SystemPlugins = ["request-logger", "custom-header"]  # 系统插件，默认加载
    ///     PluginConfigs {
    ///         request-logger { LogLevel = "Info" }
    ///         custom-header { HeaderName = "X-Custom"; HeaderValue = "test" }
    ///     }
    /// }
    /// </summary>
    private static void ProcessPluginsConfig(object value, Dictionary<string, object> result)
    {
        if (value is not Dictionary<string, object> pluginsConfig)
            return;

        var plugins = EnsureDict(result, "Plugins");

        foreach (var kv in pluginsConfig)
        {
            var key = kv.Key.ToLower();
            switch (key)
            {
                case "enabled":
                    plugins["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "plugindirectory":
                case "plugin_directory":
                case "directory":
                case "dir":
                    plugins["PluginDirectory"] = kv.Value?.ToString() ?? "plugins";
                    break;
                case "datadirectory":
                case "data_directory":
                case "datadir":
                    plugins["DataDirectory"] = kv.Value?.ToString() ?? "plugin_data";
                    break;
                case "enablehotreload":
                case "enable_hot_reload":
                case "hotreload":
                case "hot_reload":
                    plugins["EnableHotReload"] = kv.Value is bool hr ? hr : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "disabledplugins":
                case "disabled_plugins":
                case "disabled":
                    plugins["DisabledPlugins"] = ParseStringList(kv.Value);
                    break;
                case "systemplugins":
                case "system_plugins":
                case "system":
                    plugins["SystemPlugins"] = ParseStringList(kv.Value);
                    break;
                case "pluginconfigs":
                case "plugin_configs":
                case "configs":
                    // 各插件的配置
                    if (kv.Value is Dictionary<string, object> configsDict)
                    {
                        var pluginConfigs = new Dictionary<string, object>();
                        foreach (var configKv in configsDict)
                        {
                            if (configKv.Value is Dictionary<string, object> pluginConfig)
                            {
                                pluginConfigs[configKv.Key] = pluginConfig;
                            }
                        }
                        if (pluginConfigs.Count > 0)
                        {
                            plugins["PluginConfigs"] = pluginConfigs;
                        }
                    }
                    break;
                default:
                    // 检查是否是插件配置（以插件ID为键）
                    if (kv.Value is Dictionary<string, object> pluginConfigDict)
                    {
                        var pluginConfigs = EnsureDict(plugins, "PluginConfigs");
                        pluginConfigs[kv.Key] = pluginConfigDict;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// 处理 A/B 测试配置
    /// 支持格式：
    /// ABTest {
    ///     homepage-test {
    ///         Name = "首页 A/B 测试"
    ///         Enabled = true
    ///         Mode = "CookieSticky"          # Random, CookieSticky, IpHash, UserIdHash
    ///         CookieName = "ab_homepage"
    ///         CookieExpireDays = 30
    ///         Variants {
    ///             A = 70                      # 70% 流量到变体 A
    ///             B = 30                      # 30% 流量到变体 B
    ///         }
    ///         VariantTargets {
    ///             A = "cluster-a"             # 变体 A 对应的 Cluster 或 Destination
    ///             B = "cluster-b"
    ///         }
    ///         MatchPaths = ["/", "/home/*"]  # 匹配路径
    ///         ExcludePaths = ["/api/*"]      # 排除路径
    ///     }
    /// }
    /// ABTest {
    ///     homepage-test {
    ///         Mode = "CookieSticky"
    ///         Variants { A = 70; B = 30 }
    ///         VariantTargets { A = "dest1"; B = "dest2" }
    ///     }
    /// }

    /// site.example.com {
    ///     proxy {
    ///         to = "http://v1:8080 http://v2:8080"
    ///         lb_policy = "ABCookieTest"
    ///         abtest_id = "homepage-test"
    ///     }
    /// }
    /// </summary>
    private static void ProcessABTestConfig(object value, Dictionary<string, object> result)
    {
        if (value is not Dictionary<string, object> abTestConfig)
            return;

        var abTest = EnsureDict(result, "ABTest");
        var tests = new Dictionary<string, object>();

        foreach (var kv in abTestConfig)
        {
            var key = kv.Key.ToLower();

            switch (key)
            {
                case "enabled":
                    abTest["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "tests":
                    // 嵌套的 Tests { ... } 块
                    if (kv.Value is Dictionary<string, object> testsConfig)
                    {
                        foreach (var testKv in testsConfig)
                        {
                            if (testKv.Value is Dictionary<string, object> testConfig)
                            {
                                tests[testKv.Key] = ParseABTestItem(testConfig);
                            }
                        }
                    }
                    break;
                default:
                    // 直接定义的测试配置（以测试 ID 为键）
                    if (kv.Value is Dictionary<string, object> testDict)
                    {
                        tests[kv.Key] = ParseABTestItem(testDict);
                    }
                    break;
            }
        }

        if (tests.Count > 0)
        {
            abTest["Tests"] = tests;
            // 如果有测试配置但没有显式设置 Enabled，默认启用
            if (!abTest.ContainsKey("Enabled"))
            {
                abTest["Enabled"] = true;
            }
        }
    }

    /// <summary>
    /// 解析单个 A/B 测试配置项
    /// </summary>
    private static Dictionary<string, object> ParseABTestItem(Dictionary<string, object> config)
    {
        var item = new Dictionary<string, object>();

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();
            switch (key)
            {
                case "name":
                case "description":
                    item["Name"] = kv.Value?.ToString() ?? "";
                    break;
                case "enabled":
                    item["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "mode":
                    var mode = kv.Value?.ToString()?.ToLower();
                    item["Mode"] = mode switch
                    {
                        "random" => "Random",
                        "cookiesticky" or "cookie_sticky" or "cookie" or "sticky" => "CookieSticky",
                        "iphash" or "ip_hash" or "ip" => "IpHash",
                        "useridhash" or "user_id_hash" or "userid" or "user" => "UserIdHash",
                        _ => "Random"
                    };
                    break;
                case "cookiename" or "cookie_name" or "cookie":
                    item["CookieName"] = kv.Value?.ToString() ?? "ab_variant";
                    break;
                case "cookieexpiredays" or "cookie_expire_days" or "expire_days" or "expiredays":
                    if (int.TryParse(kv.Value?.ToString(), out var days))
                    {
                        item["CookieExpireDays"] = days;
                    }
                    break;
                case "variants":
                    // 变体权重配置
                    if (kv.Value is Dictionary<string, object> variantsDict)
                    {
                        var variants = new Dictionary<string, object>();
                        foreach (var vKv in variantsDict)
                        {
                            if (int.TryParse(vKv.Value?.ToString(), out var weight))
                            {
                                variants[vKv.Key] = weight;
                            }
                        }
                        item["Variants"] = variants;
                    }
                    break;
                case "varianttargets" or "variant_targets" or "targets":
                    // 变体目标配置
                    if (kv.Value is Dictionary<string, object> targetsDict)
                    {
                        var targets = new Dictionary<string, object>();
                        foreach (var tKv in targetsDict)
                        {
                            targets[tKv.Key] = tKv.Value?.ToString() ?? "";
                        }
                        item["VariantTargets"] = targets;
                    }
                    break;
                case "matchpaths" or "match_paths" or "paths" or "match":
                    item["MatchPaths"] = ParseStringList(kv.Value);
                    break;
                case "excludepaths" or "exclude_paths" or "exclude":
                    item["ExcludePaths"] = ParseStringList(kv.Value);
                    break;
            }
        }

        // 默认启用
        if (!item.ContainsKey("Enabled"))
        {
            item["Enabled"] = true;
        }

        return item;
    }

    /// <summary>
    /// 处理域名日志配置
    /// 支持格式：
    /// LyLog {
    ///     Enabled = true
    ///     Global {
    ///         Enabled = true
    ///         AccessLog = "access_${shortdate}.log"
    ///         ErrorLog = "error_${shortdate}.log"
    ///         Directory = "logs"
    ///         Level = "Info"
    ///         Format = "Text"      # Text, Json, Combined
    ///         PerfLog = false
    ///     }
    ///     Domains {
    ///         "example.com" {
    ///             Enabled = true
    ///             Output = "logs/example.com"
    ///             AccessLog = "access_${shortdate}.log"
    ///             Level = "Debug"
    ///             Format = "Json"
    ///             AlsoLogToGlobal = true
    ///             ExcludePaths = ["/health", "/metrics"]
    ///         }
    ///         "*.api.example.com" {
    ///             Output = "logs/api"
    ///             Format = "Combined"
    ///         }
    ///     }
    /// }
    /// </summary>
    private static void ProcessLyLogConfig(object value, Dictionary<string, object> result)
    {
        if (value is not Dictionary<string, object> logConfig)
            return;

        var domainLog = EnsureDict(result, "LyLog");
        var global = new Dictionary<string, object>();
        var domains = new Dictionary<string, object>();

        foreach (var kv in logConfig)
        {
            var key = kv.Key.ToLower();

            switch (key)
            {
                case "enabled":
                    domainLog["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;

                case "global":
                    // 全局日志配置
                    if (kv.Value is Dictionary<string, object> globalDict)
                    {
                        global = ParseGlobalLogConfig(globalDict);
                    }
                    break;

                case "domains":
                    // 域名日志配置
                    if (kv.Value is Dictionary<string, object> domainsDict)
                    {
                        foreach (var domainKv in domainsDict)
                        {
                            if (domainKv.Value is Dictionary<string, object> domainConfig)
                            {
                                domains[domainKv.Key] = ParseLyLogConfig(domainConfig);
                            }
                        }
                    }
                    break;

                default:
                    // 直接定义的域名配置（除了 enabled 和 global 之外的所有字典配置都视为域名配置）
                    if (kv.Value is Dictionary<string, object> directConfig)
                    {
                        domains[kv.Key] = ParseLyLogConfig(directConfig);
                    }
                    break;
            }
        }

        if (global.Count > 0)
        {
            domainLog["Global"] = global;
        }

        if (domains.Count > 0)
        {
            domainLog["Domains"] = domains;
        }

        // 如果有配置但没有显式设置 Enabled，默认启用
        if (!domainLog.ContainsKey("Enabled"))
        {
            domainLog["Enabled"] = true;
        }
    }

    /// <summary>
    /// 解析全局日志配置
    /// </summary>
    private static Dictionary<string, object> ParseGlobalLogConfig(Dictionary<string, object> config)
    {
        var result = new Dictionary<string, object>();

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();
            switch (key)
            {
                case "enabled":
                    result["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "accesslog" or "access_log":
                    result["AccessLog"] = kv.Value?.ToString() ?? "access_${shortdate}.log";
                    break;
                case "errorlog" or "error_log":
                    result["ErrorLog"] = kv.Value?.ToString() ?? "error_${shortdate}.log";
                    break;
                case "directory" or "dir":
                    result["Directory"] = kv.Value?.ToString() ?? "logs";
                    break;
                case "level":
                    result["Level"] = kv.Value?.ToString() ?? "Info";
                    break;
                case "format":
                    var format = kv.Value?.ToString()?.ToLower();
                    result["Format"] = format == "json" ? "Json" : "Text";
                    break;
                case "perflog" or "perf_log":
                    result["PerfLog"] = kv.Value is bool pb ? pb : kv.Value?.ToString()?.ToLower() == "true";
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 解析单个域名日志配置
    /// </summary>
    private static Dictionary<string, object> ParseLyLogConfig(Dictionary<string, object> config)
    {
        var result = new Dictionary<string, object>();

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();
            switch (key)
            {
                case "enabled":
                    result["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "output" or "dir" or "directory":
                    if (kv.Value?.ToString() is string output)
                        result["Output"] = output;
                    break;
                case "accesslog" or "access_log":
                    result["AccessLog"] = kv.Value?.ToString() ?? "access_${shortdate}.log";
                    break;
                case "errorlog" or "error_log":
                    result["ErrorLog"] = kv.Value?.ToString() ?? "error_${shortdate}.log";
                    break;
                case "level":
                    if (kv.Value?.ToString() is string level)
                        result["Level"] = level;
                    break;
                case "format":
                    var format = kv.Value?.ToString()?.ToLower();
                    result["Format"] = format == "json" ? "Json" : "Text";
                    break;
                case "alsologtoglobal" or "also_log_to_global" or "global":
                    result["AlsoLogToGlobal"] = kv.Value is bool gb ? gb : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "excludepaths" or "exclude_paths" or "exclude":
                    result["ExcludePaths"] = ParseStringList(kv.Value);
                    break;
                case "includepaths" or "include_paths" or "include":
                    result["IncludePaths"] = ParseStringList(kv.Value);
                    break;
            }
        }

        // 默认启用
        if (!result.ContainsKey("Enabled"))
        {
            result["Enabled"] = true;
        }

        return result;
    }

    /// <summary>
    /// 解析单个流配置
    /// </summary>
    private static Dictionary<string, object> ParseStreamConfig(object? value)
    {
        var config = new Dictionary<string, object>();
        var upstreams = new List<string>();

        if (value is string strValue)
        {
            // 简单字符串格式: "192.168.1.100:3306"
            upstreams.Add(strValue);
        }
        else if (value is List<object> listValue)
        {
            // 列表格式: ["192.168.1.100:3306", "192.168.1.101:3306"]
            foreach (var item in listValue)
            {
                if (item is string s)
                {
                    upstreams.Add(s);
                }
            }
        }
        else if (value is Dictionary<string, object> dict)
        {
            foreach (var kv in dict)
            {
                var key = kv.Key.ToLower();
                switch (key)
                {
                    case "upstreams":
                    case "upstream":
                    case "to":
                    case "targets":
                        if (kv.Value is string s)
                        {
                            upstreams.Add(s);
                        }
                        else if (kv.Value is List<object> list)
                        {
                            foreach (var item in list)
                            {
                                if (item is string str)
                                {
                                    upstreams.Add(str);
                                }
                            }
                        }
                        break;
                    case "policy":
                    case "lb":
                    case "loadbalance":
                        var policy = kv.Value?.ToString()?.ToLower();
                        config["Policy"] = policy switch
                        {
                            "random" => "Random",
                            "first" => "First",
                            _ => "RoundRobin"
                        };
                        break;
                    case "connecttimeout":
                    case "connect_timeout":
                        if (int.TryParse(kv.Value?.ToString(), out var ct))
                        {
                            config["ConnectTimeout"] = ct;
                        }
                        break;
                    case "datatimeout":
                    case "data_timeout":
                        if (int.TryParse(kv.Value?.ToString(), out var dt))
                        {
                            config["DataTimeout"] = dt;
                        }
                        break;
                    case "enabled":
                        config["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                        break;
                }
            }
        }

        if (upstreams.Count > 0)
        {
            config["Upstreams"] = upstreams;
        }

        // 默认启用
        if (!config.ContainsKey("Enabled"))
        {
            config["Enabled"] = true;
        }

        return config;
    }

    /// <summary>
    /// 验证端口键格式是否有效
    /// 支持格式: "8080", "127.0.0.1:8080", "0.0.0.0:1080"
    /// </summary>
    private static bool IsValidPortKey(string key)
    {
        // 纯端口号
        if (int.TryParse(key, out var port))
        {
            return port > 0 && port <= 65535;
        }

        // host:port 格式
        var lastColon = key.LastIndexOf(':');
        if (lastColon > 0 && lastColon < key.Length - 1)
        {
            var hostPart = key[..lastColon];
            var portPart = key[(lastColon + 1)..];
            
            // 验证端口号
            if (!int.TryParse(portPart, out port) || port <= 0 || port > 65535)
            {
                return false;
            }

            // 验证主机（IP 地址格式）
            return System.Net.IPAddress.TryParse(hostPart, out _);
        }

        return false;
    }

    /// <summary>
    /// 解析端口配置
    /// </summary>
    private static Dictionary<string, object> ParsePortConfig(object? value)
    {
        var config = new Dictionary<string, object>();
        bool explicitHttp = false, explicitHttps = false, explicitSocks5 = false;

        if (value is Dictionary<string, object> dict)
        {
            foreach (var kv in dict)
            {
                var key = kv.Key.ToLower();
                switch (key)
                {
                    case "enablehttp":
                    case "enable_http":
                    case "http":
                        config["EnableHttp"] = kv.Value is bool b1 ? b1 : kv.Value?.ToString()?.ToLower() == "true";
                        explicitHttp = true;
                        break;
                    case "enablehttps":
                    case "enable_https":
                    case "https":
                        config["EnableHttps"] = kv.Value is bool b2 ? b2 : kv.Value?.ToString()?.ToLower() == "true";
                        explicitHttps = true;
                        break;
                    case "enablesocks5":
                    case "enable_socks5":
                    case "socks5":
                    case "socks":
                        config["EnableSocks5"] = kv.Value is bool b3 ? b3 : kv.Value?.ToString()?.ToLower() == "true";
                        explicitSocks5 = true;
                        break;
                    case "requireauth":
                    case "require_auth":
                    case "auth":
                        config["RequireAuth"] = kv.Value is bool b4 ? b4 : kv.Value?.ToString()?.ToLower() == "true";
                        break;
                }
            }
            
            // 如果用户只显式启用了 SOCKS5，则 HTTP/HTTPS 默认不启用
            // 否则，如果端口被配置但未显式设置 HTTP/HTTPS，则默认启用
            bool onlySocks5 = explicitSocks5 && (bool)config.GetValueOrDefault("EnableSocks5", false) &&
                              !explicitHttp && !explicitHttps;
            
            if (!onlySocks5)
            {
                // 为显式配置的端口设置默认值
                if (!explicitHttp)
                {
                    config["EnableHttp"] = true;
                }
                if (!explicitHttps)
                {
                    config["EnableHttps"] = true;
                }
            }
        }
        else if (value is string strValue)
        {
            // 简单字符串格式: "http,https,socks5"
            var types = strValue.ToLower().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            config["EnableHttp"] = types.Contains("http");
            config["EnableHttps"] = types.Contains("https");
            config["EnableSocks5"] = types.Contains("socks5") || types.Contains("socks");
        }
        else
        {
            // 空配置或其他类型，默认启用 HTTP 和 HTTPS
            config["EnableHttp"] = true;
            config["EnableHttps"] = true;
        }

        return config;
    }

    /// <summary>
    /// 解析字符串列表
    /// </summary>
    private static List<string> ParseStringList(object? value)
    {
        var result = new List<string>();

        if (value is string str)
        {
            result.AddRange(str.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
        else if (value is List<object> list)
        {
            result.AddRange(list.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)));
        }

        return result;
    }

    /// <summary>
    /// 处理自定义 DNS 配置
    /// 支持格式：
    /// CustomDns {
    ///     Enabled = true
    ///     CacheTtlSeconds = 300
    ///     example.com = "192.168.1.100 192.168.1.101"
    ///     example.com { Addresses = ["192.168.1.100", "192.168.1.101"]; Policy = "RoundRobin" }
    ///     "*.internal.com" = "10.0.0.1"
    /// }
    /// </summary>
    private static void ProcessCustomDnsConfig(object value, Dictionary<string, object> result)
    {
        if (value is not Dictionary<string, object> dnsConfig)
            return;

        var customDns = EnsureDict(result, "CustomDns");
        var entries = new Dictionary<string, object>();

        foreach (var kv in dnsConfig)
        {
            var key = kv.Key.ToLower();

            switch (key)
            {
                case "enabled":
                    customDns["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;
                case "cachettlseconds":
                case "cache_ttl_seconds":
                case "ttl":
                    if (int.TryParse(kv.Value?.ToString(), out var ttl))
                    {
                        customDns["CacheTtlSeconds"] = ttl;
                    }
                    break;
                case "fallbackdns":
                case "fallback_dns":
                case "fallback":
                    customDns["FallbackDns"] = kv.Value?.ToString() ?? "";
                    break;
                default:
                    // 域名配置
                    var domain = kv.Key; // 保持原始大小写
                    var entry = new Dictionary<string, object>();

                    if (kv.Value is string addresses)
                    {
                        // 简单格式：example.com = "192.168.1.100 192.168.1.101"
                        var addrList = addresses.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                        entry["Addresses"] = addrList.ToList();
                        entry["Policy"] = "Random";
                    }
                    else if (kv.Value is List<object> addrObjList)
                    {
                        // 数组格式：example.com = ["192.168.1.100", "192.168.1.101"]
                        entry["Addresses"] = addrObjList.Select(x => x?.ToString() ?? "").ToList();
                        entry["Policy"] = "Random";
                    }
                    else if (kv.Value is Dictionary<string, object> entryConfig)
                    {
                        // 完整配置格式
                        foreach (var entryKv in entryConfig)
                        {
                            var entryKey = entryKv.Key.ToLower();
                            switch (entryKey)
                            {
                                case "addresses":
                                case "ips":
                                    if (entryKv.Value is string addrStr)
                                    {
                                        entry["Addresses"] = addrStr.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                                    }
                                    else if (entryKv.Value is List<object> list)
                                    {
                                        entry["Addresses"] = list.Select(x => x?.ToString() ?? "").ToList();
                                    }
                                    break;
                                case "policy":
                                    entry["Policy"] = entryKv.Value?.ToString() ?? "Random";
                                    break;
                                case "ttlseconds":
                                case "ttl_seconds":
                                case "ttl":
                                    if (int.TryParse(entryKv.Value?.ToString(), out var entryTtl))
                                    {
                                        entry["TtlSeconds"] = entryTtl;
                                    }
                                    break;
                            }
                        }
                    }

                    if (entry.ContainsKey("Addresses"))
                    {
                        entries[domain] = entry;
                    }
                    break;
            }
        }

        if (entries.Count > 0)
        {
            customDns["Entries"] = entries;
            // 如果有条目但没有显式设置 Enabled，默认启用
            if (!customDns.ContainsKey("Enabled"))
            {
                customDns["Enabled"] = true;
            }
        }
    }

    /// <summary>
    /// 处理证书配置
    /// 支持格式：
    /// 1. 默认证书：Certs { PemFile = "xxx"; KeyFile = "xxx" }
    /// 2. 域名证书：Certs { example.com { PemFile = "xxx"; KeyFile = "xxx" } }
    /// </summary>
    private static void ProcessCertsConfig(object value, List<object> certs)
    {
        if (value is not Dictionary<string, object> certsConfig)
            return;

        // 检查是否有 PemFile 和 KeyFile（默认证书）
        string? defaultPemFile = null;
        string? defaultKeyFile = null;

        foreach (var kv in certsConfig)
        {
            var key = kv.Key.ToLower();

            if (key == "pemfile" || key == "pem_file" || key == "cert")
            {
                defaultPemFile = kv.Value?.ToString();
            }
            else if (key == "keyfile" || key == "key_file" || key == "key")
            {
                defaultKeyFile = kv.Value?.ToString();
            }
            else if (kv.Value is Dictionary<string, object> domainCertConfig)
            {
                // 域名特定证书：example.com { PemFile = "xxx"; KeyFile = "xxx" }
                var host = kv.Key;
                string? pemFile = null;
                string? keyFile = null;

                foreach (var certKv in domainCertConfig)
                {
                    var certKey = certKv.Key.ToLower();
                    if (certKey == "pemfile" || certKey == "pem_file" || certKey == "cert")
                    {
                        pemFile = certKv.Value?.ToString();
                    }
                    else if (certKey == "keyfile" || certKey == "key_file" || certKey == "key")
                    {
                        keyFile = certKv.Value?.ToString();
                    }
                }

                if (!string.IsNullOrEmpty(pemFile))
                {
                    var certInfo = new Dictionary<string, object>
                    {
                        ["Host"] = host,
                        ["PemFile"] = pemFile
                    };
                    if (!string.IsNullOrEmpty(keyFile))
                    {
                        certInfo["KeyFile"] = keyFile;
                    }
                    certs.Add(certInfo);
                }
            }
        }

        // 添加默认证书（Host = "*"）
        if (!string.IsNullOrEmpty(defaultPemFile))
        {
            var certInfo = new Dictionary<string, object>
            {
                ["Host"] = "*",
                ["PemFile"] = defaultPemFile
            };
            if (!string.IsNullOrEmpty(defaultKeyFile))
            {
                certInfo["KeyFile"] = defaultKeyFile;
            }
            certs.Add(certInfo);
        }
    }

    /// <summary>
    /// 处理错误模板配置
    /// 支持格式：
    /// ErrorTemplate {
    ///     Enabled = true
    ///     ShowReason = false              # 是否显示详细原因
    ///     
    ///     # 403 Forbidden 模板
    ///     Forbidden {
    ///         Type = "File"               # File, Inline, Default
    ///         FilePath = "templates/403.html"
    ///         ContentType = "text/html; charset=utf-8"
    ///     }
    ///     
    ///     # 内联模板示例
    ///     NotFound {
    ///         Type = "Inline"
    ///         Content = "<h1>404 Not Found</h1><p>Page not found: {path}</p>"
    ///     }
    ///     
    ///     # 429 Too Many Requests
    ///     TooManyRequests {
    ///         Type = "File"
    ///         FilePath = "templates/429.html"
    ///     }
    ///     
    ///     # 自定义状态码
    ///     Custom {
    ///         "418" {
    ///             Type = "Inline"
    ///             Content = "<h1>I'm a teapot</h1>"
    ///         }
    ///     }
    /// }
    /// </summary>
    private static void ProcessErrorTemplateConfig(object value, Dictionary<string, object> result)
    {
        if (value is not Dictionary<string, object> config)
            return;

        var errorTemplate = EnsureDict(result, "ErrorTemplate");

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();

            switch (key)
            {
                case "enabled":
                    errorTemplate["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                    break;

                case "showreason" or "show_reason":
                    errorTemplate["ShowReason"] = kv.Value is bool sr ? sr : kv.Value?.ToString()?.ToLower() == "true";
                    break;

                case "forbidden" or "403":
                    if (kv.Value is Dictionary<string, object> forbiddenConfig)
                        errorTemplate["Forbidden"] = ParseErrorTemplateItem(forbiddenConfig);
                    break;

                case "notfound" or "not_found" or "404":
                    if (kv.Value is Dictionary<string, object> notFoundConfig)
                        errorTemplate["NotFound"] = ParseErrorTemplateItem(notFoundConfig);
                    break;

                case "toomanyrequests" or "too_many_requests" or "429":
                    if (kv.Value is Dictionary<string, object> tooManyConfig)
                        errorTemplate["TooManyRequests"] = ParseErrorTemplateItem(tooManyConfig);
                    break;

                case "internalerror" or "internal_error" or "500":
                    if (kv.Value is Dictionary<string, object> internalConfig)
                        errorTemplate["InternalError"] = ParseErrorTemplateItem(internalConfig);
                    break;

                case "badgateway" or "bad_gateway" or "502":
                    if (kv.Value is Dictionary<string, object> badGatewayConfig)
                        errorTemplate["BadGateway"] = ParseErrorTemplateItem(badGatewayConfig);
                    break;

                case "serviceunavailable" or "service_unavailable" or "503":
                    if (kv.Value is Dictionary<string, object> unavailableConfig)
                        errorTemplate["ServiceUnavailable"] = ParseErrorTemplateItem(unavailableConfig);
                    break;

                case "custom":
                    if (kv.Value is Dictionary<string, object> customConfig)
                    {
                        var custom = new Dictionary<string, object>();
                        foreach (var customKv in customConfig)
                        {
                            if (customKv.Value is Dictionary<string, object> customItemConfig)
                                custom[customKv.Key] = ParseErrorTemplateItem(customItemConfig);
                        }
                        errorTemplate["Custom"] = custom;
                    }
                    break;
            }
        }

        // 如果有配置但没有显式设置 Enabled，默认启用
        if (!errorTemplate.ContainsKey("Enabled"))
        {
            errorTemplate["Enabled"] = true;
        }
    }

    /// <summary>
    /// 解析单个错误模板配置项
    /// </summary>
    private static Dictionary<string, object> ParseErrorTemplateItem(Dictionary<string, object> config)
    {
        var result = new Dictionary<string, object>();

        foreach (var kv in config)
        {
            var key = kv.Key.ToLower();

            switch (key)
            {
                case "type":
                    var typeStr = kv.Value?.ToString()?.ToLower();
                    result["Type"] = typeStr switch
                    {
                        "file" => "File",
                        "inline" => "Inline",
                        _ => "Default"
                    };
                    break;

                case "filepath" or "file_path" or "file" or "path":
                    result["FilePath"] = kv.Value?.ToString() ?? "";
                    break;

                case "content" or "template":
                    result["Content"] = kv.Value?.ToString() ?? "";
                    break;

                case "contenttype" or "content_type" or "content-type":
                    result["ContentType"] = kv.Value?.ToString() ?? "text/html; charset=utf-8";
                    break;

                case "headers":
                    if (kv.Value is Dictionary<string, object> headersConfig)
                    {
                        var headers = new Dictionary<string, string>();
                        foreach (var headerKv in headersConfig)
                        {
                            headers[headerKv.Key] = headerKv.Value?.ToString() ?? "";
                        }
                        result["Headers"] = headers;
                    }
                    break;
            }
        }

        // 如果指定了文件路径但没有指定类型，默认为 File
        if (result.ContainsKey("FilePath") && !result.ContainsKey("Type"))
        {
            result["Type"] = "File";
        }
        // 如果指定了内容但没有指定类型，默认为 Inline
        else if (result.ContainsKey("Content") && !result.ContainsKey("Type"))
        {
            result["Type"] = "Inline";
        }

        return result;
    }

    /// <summary>
    /// 处理 CC 防护规则配置
    /// 以规则名称为 key，规则详情为子块，转换为 WafInfos.CcRules 列表
    ///
    /// 支持格式：
    /// CcRules {
    ///     频繁访问API限制 {
    ///         Enabled = true
    ///         Type = FrequentAccess        # FrequentAccess | FrequentAttack | FrequentError
    ///         Period = 10                  # 统计周期（秒）
    ///         Threshold = 100              # 触发阈值
    ///         Action = Block               # Block | Captcha | Reject | RateLimit | LogOnly
    ///         ActionSeconds = 600          # 动作持续时间（秒）
    ///         Priority = 1                 # 优先级（越小越高）
    ///         Conditions {
    ///             UrlPath StartsWith ["/api/", "/v2/"]
    ///             Method Equal ["POST", "PUT"]
    ///         }
    ///     }
    ///     高频攻击封禁 {
    ///         Type = FrequentAttack
    ///         Threshold = 5
    ///         Action = Block
    ///         ActionSeconds = 1800
    ///     }
    /// }
    /// </summary>
    private static void ProcessCcRulesConfig(object value, Dictionary<string, object> wafInfos)
    {
        if (value is not Dictionary<string, object> rulesConfig)
            return;

        var rulesList = new List<object>();

        foreach (var ruleKv in rulesConfig)
        {
            var ruleName = ruleKv.Key;

            if (ruleKv.Value is not Dictionary<string, object> ruleConfig)
                continue;

            var rule = new Dictionary<string, object>
            {
                ["Name"] = ruleName,
                ["Enabled"] = true
            };

            List<object>? conditions = null;

            foreach (var kv in ruleConfig)
            {
                var key = kv.Key.ToLower();
                switch (key)
                {
                    case "enabled":
                        rule["Enabled"] = kv.Value is bool b ? b : kv.Value?.ToString()?.ToLower() == "true";
                        break;

                    case "type":
                        rule["Type"] = NormalizeCcEnum(kv.Value?.ToString() ?? "",
                            ["FrequentAccess", "FrequentAttack", "FrequentError"], "FrequentAccess")!;
                        break;

                    case "period":
                        if (int.TryParse(kv.Value?.ToString(), out var period))
                            rule["Period"] = period;
                        break;

                    case "threshold":
                        if (int.TryParse(kv.Value?.ToString(), out var threshold))
                            rule["Threshold"] = threshold;
                        break;

                    case "action":
                        rule["Action"] = NormalizeCcEnum(kv.Value?.ToString() ?? "",
                            ["Block", "Captcha", "Reject", "RateLimit", "LogOnly"], "Captcha")!;
                        break;

                    case "actionseconds" or "action_seconds" or "duration":
                        if (int.TryParse(kv.Value?.ToString(), out var duration))
                            rule["ActionSeconds"] = duration;
                        break;

                    case "priority":
                        if (int.TryParse(kv.Value?.ToString(), out var priority))
                            rule["Priority"] = priority;
                        break;

                    case "conditions" or "condition":
                        conditions = ParseCcConditions(kv.Value);
                        break;
                }
            }

            if (conditions != null && conditions.Count > 0)
            {
                rule["Conditions"] = conditions;
            }

            rulesList.Add(rule);
        }

        if (rulesList.Count > 0)
        {
            wafInfos["CcRules"] = rulesList;
        }
    }

    /// <summary>
    /// 解析 CC 条件配置
    /// 支持格式：
    /// Conditions {
    ///     UrlPath StartsWith ["/api/", "/v2/"]       # Target Operator Values
    ///     Method Equal ["POST"]
    ///     UserAgent Contains ["bot", "spider"]
    /// }
    ///
    /// 或嵌套格式：
    /// Conditions {
    ///     cond1 { Target = UrlPath; Operator = StartsWith; Values = ["/api/"] }
    /// }
    /// </summary>
    private static List<object> ParseCcConditions(object? value)
    {
        var result = new List<object>();

        if (value is not Dictionary<string, object> condConfig)
            return result;

        foreach (var kv in condConfig)
        {
            if (kv.Value is Dictionary<string, object> detailConfig)
            {
                // 嵌套格式: cond1 { Target = UrlPath; Operator = StartsWith; Values = ["/api/"] }
                var condition = new Dictionary<string, object>();
                foreach (var dkv in detailConfig)
                {
                    var dkey = dkv.Key.ToLower();
                    switch (dkey)
                    {
                        case "target":
                            condition["Target"] = NormalizeCcEnum(dkv.Value?.ToString() ?? "",
                                ["UrlPath", "FullUrl", "Method", "ContentType", "UserAgent",
                                 "Referer", "Header", "QueryParam", "Cookie", "ClientIp", "StatusCode"],
                                "UrlPath")!;
                            break;
                        case "operator" or "op":
                            condition["Operator"] = NormalizeCcEnum(dkv.Value?.ToString() ?? "",
                                ["Equal", "NotEqual", "Contains", "NotContains",
                                 "StartsWith", "EndsWith", "Regex", "Exists", "NotExists"],
                                "Equal")!;
                            break;
                        case "values" or "value":
                            condition["Values"] = ParseStringList(dkv.Value);
                            break;
                    }
                }
                if (condition.Count > 0)
                    result.Add(condition);
            }
            else
            {
                // 简写格式: "UrlPath StartsWith ["/api/"]" → key 是 Target, 值包含 "Operator Values"
                // key = "UrlPath", value = "StartsWith [\"/api/\"]" 或 list
                var target = NormalizeCcEnum(kv.Key,
                    ["UrlPath", "FullUrl", "Method", "ContentType", "UserAgent",
                     "Referer", "Header", "QueryParam", "Cookie", "ClientIp", "StatusCode"],
                    null);

                if (target == null)
                    continue;

                var condition = new Dictionary<string, object> { ["Target"] = target };

                if (kv.Value is string strVal)
                {
                    // "StartsWith /api/" 或 "StartsWith [\"/api/\", \"/v2/\"]"
                    var parts = strVal.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        condition["Operator"] = NormalizeCcEnum(parts[0],
                            ["Equal", "NotEqual", "Contains", "NotContains",
                             "StartsWith", "EndsWith", "Regex", "Exists", "NotExists"],
                            "Equal")!;

                        if (parts.Length >= 2)
                        {
                            condition["Values"] = ParseStringList(parts[1]);
                        }
                    }
                }
                else if (kv.Value is List<object> listVal)
                {
                    // 解析器将 "UrlPath StartsWith ["/api/", "/v2/"]" 解析为:
                    // key = "UrlPath", value = ["StartsWith", ["/api/", "/v2/"]]
                    var operatorStr = listVal.FirstOrDefault(x => x is string) as string;
                    var valuesArr = listVal.FirstOrDefault(x => x is List<object>) as List<object>;

                    if (operatorStr != null)
                    {
                        condition["Operator"] = NormalizeCcEnum(operatorStr,
                            ["Equal", "NotEqual", "Contains", "NotContains",
                             "StartsWith", "EndsWith", "Regex", "Exists", "NotExists"],
                            "Equal")!;
                    }
                    else
                    {
                        condition["Operator"] = "Equal";
                    }

                    if (valuesArr != null)
                    {
                        condition["Values"] = valuesArr.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                    }
                    else
                    {
                        // 全是字符串，没有嵌套数组 → 当做值列表
                        condition["Values"] = listVal.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                    }
                }

                result.Add(condition);
            }
        }

        return result;
    }

    /// <summary>
    /// 枚举值归一化：忽略大小写匹配有效枚举值
    /// </summary>
    private static string? NormalizeCcEnum(string input, string[] validValues, string? defaultValue)
    {
        foreach (var v in validValues)
        {
            if (v.Equals(input, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return defaultValue;
    }
}
