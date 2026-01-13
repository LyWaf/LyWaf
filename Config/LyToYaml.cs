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

        switch (value)
        {
            case List<object> list:
                sb.AppendLine($"{prefix}{key}:");
                foreach (var item in list)
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
                break;

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
        var listens = new List<object>();
        var certs = new List<object>();
        var routes = new Dictionary<string, object>();
        var clusters = new Dictionary<string, object>();
        var routeIndex = 1;
        var clusterIndex = 1;

        foreach (var kv in config)
        {
            var key = kv.Key;
            var value = kv.Value;

            // 检查是否是站点块（地址格式）
            if (IsSiteAddress(key))
            {
                // 站点块 - 解析地址和内容
                var (routeDict, clusterList, listenList, certList) = ProcessSiteBlock(key, value, ref routeIndex, ref clusterIndex);
                foreach (var r in routeDict)
                {
                    routes[r.Key] = r.Value;
                }
                foreach (var c in clusterList)
                {
                    clusters[c.Key] = c.Value;
                }
                listens.AddRange(listenList);
                certs.AddRange(certList);
            }
            else if (key.StartsWith("(") && key.EndsWith(")"))
            {
                // 代码片段 - 已在解析阶段处理
                continue;
            }
            else
            {
                // 其他顶级配置（全局选项或直接配置）
                ProcessGlobalOption(key, value, result, wafInfos, listens, certs);
            }
        }

        // 构建 WafInfos
        if (listens.Count > 0)
        {
            wafInfos["Listens"] = listens;
        }
        if (certs.Count > 0)
        {
            wafInfos["Certs"] = certs;
        }
        if (wafInfos.Count > 0)
        {
            result["WafInfos"] = wafInfos;
        }

        // 构建 ReverseProxy
        if (routes.Count > 0 || clusters.Count > 0)
        {
            var reverseProxy = new Dictionary<string, object>();
            if (routes.Count > 0)
            {
                reverseProxy["Routes"] = routes;
            }
            if (clusters.Count > 0)
            {
                reverseProxy["Clusters"] = clusters;
            }
            result["ReverseProxy"] = reverseProxy;
        }

        return result;
    }

    /// <summary>
    /// 判断是否是站点地址格式
    /// 支持: example.com, :8080, https://example.com, http://example.com, *.example.com
    /// </summary>
    private static bool IsSiteAddress(string key)
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

        // localhost
        if (key.ToLower() == "localhost")
            return true;

        // IP 地址格式
        if (System.Net.IPAddress.TryParse(key.Split(':')[0], out _))
            return true;

        return false;
    }

    /// <summary>
    /// 处理站点块
    /// </summary>
    private static (Dictionary<string, object> routes, Dictionary<string, object> clusters, List<object> listens, List<object> certs)
        ProcessSiteBlock(string address, object content, ref int routeIndex, ref int clusterIndex)
    {
        var routes = new Dictionary<string, object>();
        var clusters = new Dictionary<string, object>();
        var listens = new List<object>();
        var certs = new List<object>();

        // 解析地址 - 可能包含多个域名/地址
        var addresses = address.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hosts = new List<string>();
        var listenPort = 0;
        var isHttps = false;

        foreach (var addr in addresses)
        {
            var parsed = ParseSiteAddress(addr);
            if (parsed.Host != null)
            {
                hosts.Add(parsed.Host);
            }
            if (parsed.Port > 0)
            {
                listenPort = parsed.Port;
            }
            if (parsed.IsHttps)
            {
                isHttps = true;
            }
        }

        // 添加监听端口（如果是端口格式的站点）
        if (listenPort > 0 && hosts.Count == 0)
        {
            listens.Add(new Dictionary<string, object>
            {
                ["Host"] = "0.0.0.0",
                ["Port"] = listenPort,
                ["IsHttps"] = isHttps
            });
        }

        // 处理站点内容
        if (content is Dictionary<string, object> siteContent)
        {
            var routeId = $"route{routeIndex++}";
            var clusterId = $"cluster{clusterIndex++}";

            // 路由配置 - 以 routeId 为 key
            var routeConfig = new Dictionary<string, object>
            {
                ["ClusterId"] = clusterId,
                
            };

            var match = new Dictionary<string, object>();

            // 设置主机匹配
            if (hosts.Count > 0)
            {
                match["Hosts"] = hosts;
            }

            // 处理站点指令
            var destinations = new Dictionary<string, object>();
            var clusterConfig = new Dictionary<string, object>();

            foreach (var directive in siteContent)
            {
                switch (directive.Key.ToLower())
                {
                    case "reverse_proxy":
                        // 反向代理指令
                        var upstreams = ParseUpstreams(directive.Value);
                        var destIndex = 1;
                        foreach (var upstream in upstreams)
                        {
                            destinations[$"dest{destIndex++}"] = new Dictionary<string, object>
                            {
                                ["Address"] = upstream
                            };
                        }
                        break;

                    case "root":
                        // 根目录（文件服务）
                        // 暂不支持
                        break;

                    case "tls":
                        // TLS 配置
                        if (directive.Value is Dictionary<string, object> tlsConfig)
                        {
                            if (tlsConfig.TryGetValue("cert", out var cert) && 
                                tlsConfig.TryGetValue("key", out var key))
                            {
                                var certInfo = new Dictionary<string, object>
                                {
                                    ["PemFile"] = cert.ToString()!,
                                    ["KeyFile"] = key.ToString()!
                                };
                                if (hosts.Count > 0)
                                {
                                    certInfo["Host"] = hosts[0];
                                }
                                certs.Add(certInfo);
                            }
                        }
                        break;

                    case "lb_policy":
                    case "load_balancing_policy":
                        clusterConfig["LoadBalancingPolicy"] = directive.Value.ToString()!;
                        break;

                    case "health_check":
                        if (directive.Value is Dictionary<string, object> hcConfig)
                        {
                            clusterConfig["HealthCheck"] = hcConfig;
                        }
                        break;

                    case "path":
                    case "path_prefix":
                        match["Path"] = directive.Value.ToString()!;
                        break;

                    default:
                        // 其他指令作为元数据或忽略
                        break;
                }
            }

            if (match.Count > 0)
            {
                routeConfig["Match"] = match;
            } else {
                routeConfig["Match"] = new Dictionary<string, string> {
                    ["Path"] = "/{**catch-all}"
                };
            }

            if (destinations.Count > 0)
            {
                // 以 routeId 为 key 添加路由
                routes[routeId] = routeConfig;
                clusterConfig["Destinations"] = destinations;
                clusters[clusterId] = clusterConfig;
            }
        }

        return (routes, clusters, listens, certs);
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
        List<object> listens,
        List<object> certs)
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
                listens.Add(new Dictionary<string, object>
                {
                    ["Host"] = "0.0.0.0",
                    ["Port"] = int.Parse(value.ToString()!),
                    ["IsHttps"] = false
                });
                break;

            case "https_port":
                listens.Add(new Dictionary<string, object>
                {
                    ["Host"] = "0.0.0.0",
                    ["Port"] = int.Parse(value.ToString()!),
                    ["IsHttps"] = true
                });
                break;

            case "debug":
                // 调试模式
                EnsureDict(result, "Logging")["Level"] = "Debug";
                break;

            case "tls":
                // 全局 TLS 配置
                if (value is Dictionary<string, object> tlsConfig)
                {
                    if (tlsConfig.TryGetValue("cert", out var cert) && 
                        tlsConfig.TryGetValue("key", out var keyVal))
                    {
                        certs.Add(new Dictionary<string, object>
                        {
                            ["PemFile"] = cert.ToString()!,
                            ["KeyFile"] = keyVal.ToString()!
                        });
                    }
                }
                break;

            default:
                // 其他配置直接映射（首字母大写）
                var normalizedKey = char.ToUpper(key[0]) + key[1..];
                result[normalizedKey] = value;
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
}
