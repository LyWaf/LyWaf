
using System.Collections.Generic;
using System.Linq;
using LyWaf.Services.WafInfo;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace LyWaf.Transformer
{

    // 1. 创建自定义请求转换器
    public class WafTrans : ITransformProvider
    {
        /// <summary>
        /// 从请求头中提取现有的 Forwarded 数据（同时解析 Forwarded 和 X-Forwarded-* 格式）
        /// </summary>
        private static (List<string> forList, string? proto, string? host) ExtractForwardedData(
            System.Net.Http.Headers.HttpRequestHeaders headers)
        {
            var forList = new List<string>();
            string? proto = null;
            string? host = null;

            // 解析 Forwarded 头（RFC 7239）
            if (headers.TryGetValues("Forwarded", out var forwardedValues))
            {
                var forwardedStr = string.Join(", ", forwardedValues);
                foreach (var entry in forwardedStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var part in entry.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Trim();
                        if (kv.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                        {
                            forList.Add(kv[4..].Trim('"', ' '));
                        }
                        else if (kv.StartsWith("proto=", StringComparison.OrdinalIgnoreCase) && proto == null)
                        {
                            proto = kv[6..].Trim('"', ' ');
                        }
                        else if (kv.StartsWith("host=", StringComparison.OrdinalIgnoreCase) && host == null)
                        {
                            host = kv[5..].Trim('"', ' ');
                        }
                    }
                }
            }

            // 解析 X-Forwarded-* 头
            if (headers.TryGetValues("X-Forwarded-For", out var xForValues))
            {
                foreach (var ip in string.Join(", ", xForValues).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    forList.Add(ip.Trim());
                }
            }
            if (proto == null && headers.TryGetValues("X-Forwarded-Proto", out var xProtoValues))
            {
                proto = string.Join("", xProtoValues).Trim();
            }
            if (host == null && headers.TryGetValues("X-Forwarded-Host", out var xHostValues))
            {
                host = string.Join("", xHostValues).Trim();
            }

            return (forList, proto, host);
        }

        /// <summary>
        /// 清除所有 Forwarded 相关的头
        /// </summary>
        private static void ClearForwardedHeaders(System.Net.Http.Headers.HttpRequestHeaders headers)
        {
            headers.Remove("Forwarded");
            headers.Remove("X-Forwarded-For");
            headers.Remove("X-Forwarded-Proto");
            headers.Remove("X-Forwarded-Host");
        }

        /// <summary>
        /// 处理 Forwarded 头（兼容 RFC 7239 和 X-Forwarded-* 格式之间的转换）
        /// </summary>
        private static void ProcessForwardedHeaders(
            System.Net.Http.Headers.HttpRequestHeaders headers,
            ForwardedInfo config,
            HttpContext httpContext)
        {
            var request = httpContext.Request;
            var connection = httpContext.Connection;
            
            var clientIp = config.For ?? connection.RemoteIpAddress?.ToString() ?? "unknown";
            var proto = config.Proto ?? request.Scheme;
            var host = config.Host ?? request.Host.ToString();
            var method = config.Method?.ToLower() ?? "append";

            // 提取现有的 Forwarded 数据
            var (existingForList, existingProto, existingHost) = ExtractForwardedData(headers);
            
            // 清除所有旧头
            ClearForwardedHeaders(headers);

            if (config.IsX)
            {
                // 输出 X-Forwarded-* 格式
                if (method == "set")
                {
                    headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                    headers.TryAddWithoutValidation("X-Forwarded-Proto", proto);
                    headers.TryAddWithoutValidation("X-Forwarded-Host", host);
                }
                else if (method == "append")
                {
                    existingForList.Add(clientIp);
                    headers.TryAddWithoutValidation("X-Forwarded-For", string.Join(", ", existingForList));
                    headers.TryAddWithoutValidation("X-Forwarded-Proto", existingProto ?? proto);
                    headers.TryAddWithoutValidation("X-Forwarded-Host", existingHost ?? host);
                }
            }
            else
            {
                // 输出 Forwarded 格式（RFC 7239）
                if (method == "set")
                {
                    var fullEntry = $"proto={proto}; host=\"{host}\"; for={clientIp}; by=lywaf";
                    headers.TryAddWithoutValidation("Forwarded", fullEntry);
                }
                else if (method == "append")
                {
                    var entries = new List<string>();
                    
                    // 将现有数据转换为 Forwarded 条目
                    foreach (var forIp in existingForList)
                    {
                        var entryParts = new List<string>();
                        if (existingProto != null) entryParts.Add($"proto={existingProto}");
                        if (existingHost != null) entryParts.Add($"host=\"{existingHost}\"");
                        entryParts.Add($"for={forIp}");
                        entries.Add(string.Join("; ", entryParts));
                    }
                    
                    // 添加当前代理的条目
                    if (entries.Count > 0)
                    {
                        entries.Add($"for={clientIp}; by=lywaf");
                        headers.TryAddWithoutValidation("Forwarded", string.Join(", ", entries));
                    }
                    else
                    {
                        var fullEntry = $"proto={proto}; host=\"{host}\"; for={clientIp}; by=lywaf";
                        headers.TryAddWithoutValidation("Forwarded", fullEntry);
                    }
                }
            }
        }

        public void ValidateRoute(TransformRouteValidationContext context)
        {
            if (context.Route.Metadata?.TryGetValue("CustomMetadata", out var value) ??
                false)
            {
                if (string.IsNullOrEmpty(value))
                {
                    context.Errors.Add(new ArgumentException(
                        "A non-empty CustomMetadata value is required"));
                }
            }
        }

        public void ValidateCluster(TransformClusterValidationContext context)
        {
            // Check all clusters for a custom property and validate the associated
            // transform data.
            if (context.Cluster.Metadata?.TryGetValue("CustomMetadata", out var value)
                ?? false)
            {
                if (string.IsNullOrEmpty(value))
                {
                    context.Errors.Add(new ArgumentException(
                        "A non-empty CustomMetadata value is required"));
                }
            }
        }

        public void Apply(TransformBuilderContext transformBuildContext)
        {
            var services = transformBuildContext.Services.GetRequiredService<IWafInfoService>();

            var forwardedConfig = services.GetOptions().Forwarded;
            var isOpen = forwardedConfig != null && forwardedConfig.Method?.ToLower() != "none";
            if (isOpen)
            {
                transformBuildContext.UseDefaultForwarders = false;
            }

            transformBuildContext.AddRequestTransform(async transformContext =>
            {
                foreach (var (k, v) in services.GetOptions().HeaderUps)
                {
                    transformContext.HttpContext.Request.Headers[k] = v;
                }
                transformContext.HttpContext.Items.Add("ProxyDestUrl", transformContext.DestinationPrefix);

                // 处理 Forwarded 头
                if (isOpen)
                {
                    ProcessForwardedHeaders(transformContext.ProxyRequest.Headers, forwardedConfig!, transformContext.HttpContext);
                }
            });

            transformBuildContext.AddResponseTransform(async transformContext =>
            {
                foreach (var (k, v) in services.GetOptions().HeaderDowns)
                {
                    transformContext.HttpContext.Response.Headers[k] = v;
                }
                transformContext.HttpContext.Response.Headers.Server = "LyWaf";
            });
        }
    }

}