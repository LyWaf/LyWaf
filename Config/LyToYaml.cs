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

                if (upstreams.Count > 0)
                {
                    // 获取或创建 cluster
                    var clusterId = GetOrCreateCluster(upstreams, clusterConfig, ctx);

                    // 创建路由
                    var routeId = ctx.NextRouteId();
                    var normalizedPath = NormalizePath(path);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = normalizedPath
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }

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
                    var prefix = PathToFileServerPrefix(path);
                    var fileServerId = BuildFileServerConfig(blockFileServerConfig, prefix, hosts, ctx);

                    // 创建文件服务路由，将 * 替换为 {**file-all}
                    var matchPath = NormalizeFileServerPath(path);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = matchPath
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
                        ["Order"] = ctx.NextRouteOrder(matchPath, hosts.Count > 0)
                    };

                    ctx.Routes[fileServerId] = routeConfig;
                }
                else if (blockHasRespond)
                {
                    // respond 路由 - 使用 simpleres_xxx 作为路由 ID
                    var routeId = BuildSimpleResConfig(blockRespondConfig, ctx);
                    var normalizedPath = NormalizePath(path);
                    var match = new Dictionary<string, object>
                    {
                        ["Path"] = normalizedPath
                    };
                    if (hosts.Count > 0)
                    {
                        match["Hosts"] = hosts;
                    }

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
            destinations[$"dest{destIndex++}"] = new Dictionary<string, object>
            {
                ["Address"] = upstream
            };
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
    /// 规范化路径格式
    /// </summary>
    private static string NormalizePath(string path)
    {
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
}
