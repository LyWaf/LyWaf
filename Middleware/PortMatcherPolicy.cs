using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using NLog;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;

/// <summary>
/// 端口匹配策略
/// 在 EndpointRoutingMiddleware 选择候选路由后、最终选择前执行
/// 根据请求端口过滤候选端点，解决 AmbiguousMatchException
/// </summary>
public class PortMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    // 优先级设为较低值，在其他策略之后执行（让 YARP 的策略先执行）
    public override int Order => 1000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        // 只要有 YARP 路由就应用此策略
        // YARP 使用 RouteModel 作为端点元数据
        var hasYarpRoute = endpoints.Any(e => e.Metadata.GetMetadata<RouteModel>() != null);
        
        _logger.Trace("PortMatcherPolicy.AppliesToEndpoints: {Result}, 端点数: {Count}", 
            hasYarpRoute, endpoints.Count);
        
        return hasYarpRoute;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        var requestPort = httpContext.Connection.LocalPort;
        var requestHost = httpContext.Request.Host.Host;
        
        _logger.Debug("PortMatcherPolicy.ApplyAsync: 请求 {Host}:{Port}, 候选端点数: {Count}", 
            requestHost, requestPort, candidates.Count);

        // 记录匹配的端点
        var specificMatches = new List<int>(); // 精确匹配（带端口）
        var wildcardMatches = new List<int>(); // 通配符匹配
        var otherMatches = new List<int>(); // 非 YARP 路由

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
                continue;

            var endpoint = candidates[i].Endpoint;
            
            // 获取 YARP 的 RouteModel 元数据
            var routeModel = endpoint.Metadata.GetMetadata<RouteModel>();
            if (routeModel == null)
            {
                // 非 YARP 路由，保持有效
                otherMatches.Add(i);
                continue;
            }

            var hosts = routeModel.Config.Match.Hosts;
            var routeId = routeModel.Config.RouteId;
            
            _logger.Trace("PortMatcherPolicy: 检查路由 {RouteId}, Hosts={Hosts}", 
                routeId, hosts != null ? string.Join(",", hosts) : "null");
            
            if (hosts == null || hosts.Count == 0)
            {
                // 没有 Hosts 限制，作为通配符匹配
                wildcardMatches.Add(i);
                continue;
            }

            // 检查是否匹配
            var matchResult = CheckHostMatch(hosts, requestHost, requestPort);
            if (matchResult == MatchResult.Specific)
            {
                specificMatches.Add(i);
                _logger.Debug("PortMatcherPolicy: 精确匹配路由 {RouteId}", routeId);
            }
            else if (matchResult == MatchResult.Wildcard)
            {
                wildcardMatches.Add(i);
                _logger.Debug("PortMatcherPolicy: 通配符匹配路由 {RouteId}", routeId);
            }
            else
            {
                // 不匹配，标记为无效
                candidates.SetValidity(i, false);
                _logger.Debug("PortMatcherPolicy: 排除路由 {RouteId}, Hosts={Hosts}, 请求端口={Port}", 
                    routeId, string.Join(",", hosts), requestPort);
            }
        }

        // 如果有精确匹配，禁用通配符匹配
        if (specificMatches.Count > 0 && wildcardMatches.Count > 0)
        {
            foreach (var idx in wildcardMatches)
            {
                candidates.SetValidity(idx, false);
                var routeModel = candidates[idx].Endpoint.Metadata.GetMetadata<RouteModel>();
                _logger.Debug("PortMatcherPolicy: 排除通配符路由 {RouteId}，因为有精确匹配", 
                    routeModel?.Config.RouteId);
            }
        }
        
        _logger.Debug("PortMatcherPolicy: 完成筛选，精确匹配={Specific}, 通配符={Wildcard}, 其他={Other}",
            specificMatches.Count, wildcardMatches.Count, otherMatches.Count);

        return Task.CompletedTask;
    }

    private enum MatchResult
    {
        NoMatch,
        Wildcard,
        Specific
    }

    private static MatchResult CheckHostMatch(IReadOnlyList<string> hosts, string requestHost, int requestPort)
    {
        var bestMatch = MatchResult.NoMatch;

        foreach (var host in hosts)
        {
            var result = CheckSingleHostMatch(host, requestHost, requestPort);
            if (result == MatchResult.Specific)
            {
                return MatchResult.Specific; // 找到精确匹配，直接返回
            }
            if (result == MatchResult.Wildcard && bestMatch == MatchResult.NoMatch)
            {
                bestMatch = MatchResult.Wildcard;
            }
        }

        return bestMatch;
    }

    // localhost, 127.0.0.1, [::1] 互相等价
    private static readonly HashSet<string> LocalhostAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "[::1]",
        "::1"
    };

    /// <summary>
    /// 判断是否为本地主机地址
    /// </summary>
    private static bool IsLocalhost(string host)
    {
        return LocalhostAliases.Contains(host);
    }

    private static MatchResult CheckSingleHostMatch(string pattern, string requestHost, int requestPort)
    {
        string patternHost;
        int? patternPort = null;

        // 处理 IPv6 地址带端口的情况，如 [::1]:8080
        if (pattern.StartsWith('['))
        {
            var bracketEnd = pattern.IndexOf(']');
            if (bracketEnd > 0)
            {
                patternHost = pattern[..(bracketEnd + 1)];
                if (bracketEnd + 1 < pattern.Length && pattern[bracketEnd + 1] == ':')
                {
                    if (int.TryParse(pattern[(bracketEnd + 2)..], out var port))
                    {
                        patternPort = port;
                    }
                }
            }
            else
            {
                patternHost = pattern;
            }
        }
        else if (pattern.Contains(':'))
        {
            var colonIndex = pattern.LastIndexOf(':');
            patternHost = pattern[..colonIndex];
            if (int.TryParse(pattern[(colonIndex + 1)..], out var port))
            {
                patternPort = port;
            }
        }
        else
        {
            patternHost = pattern;
        }

        // 检查端口匹配
        if (patternPort.HasValue && patternPort.Value != requestPort)
        {
            return MatchResult.NoMatch;
        }

        // 检查主机名匹配
        bool hostMatches;
        bool isWildcard = false;

        if (patternHost == "*")
        {
            hostMatches = true;
            isWildcard = true;
        }
        else if (patternHost.StartsWith("*."))
        {
            var suffix = patternHost[1..]; // .example.com
            hostMatches = requestHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                          requestHost.Equals(patternHost[2..], StringComparison.OrdinalIgnoreCase);
            isWildcard = true;
        }
        else if (IsLocalhost(patternHost) && IsLocalhost(requestHost))
        {
            // localhost, 127.0.0.1, [::1] 互相匹配
            hostMatches = true;
        }
        else
        {
            hostMatches = requestHost.Equals(patternHost, StringComparison.OrdinalIgnoreCase);
        }

        if (!hostMatches)
        {
            return MatchResult.NoMatch;
        }

        // 有端口的精确匹配优先级更高
        if (patternPort.HasValue && !isWildcard)
        {
            return MatchResult.Specific;
        }

        return isWildcard ? MatchResult.Wildcard : MatchResult.Specific;
    }
}
