
using LyWaf.Services.AccessControl;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using NLog;

namespace LyWaf.Utils;

public class StatisticUtil
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public static async Task DoStatisticRequest(HttpContext context, string destUrl, long costTime)
    {
        var statisticService = context.RequestServices.GetRequiredService<IStatisticService>();
        var path = await statisticService.GetMatchPath(context.Request.Path);
        _logger.Info("统计访问: 路径:{}, 实际归因:{}", context.Request.Path, path);
        bool func(string url, IpStatistic val, bool updateLastAccess = false)
        {
            if (val.UrlCostTime.TryGetValue(path, out var sub))
            {
                sub.IncrTime(costTime);
            }
            else
            {
                val.UrlCostTime[path] = new StaCountTime
                {
                    Count = 1,
                    UseTime = costTime,
                };
            }
            val.CountTime.IncrTime(costTime);
            
            // 更新最后访问时间（仅对客户端统计有效）
            if (updateLastAccess)
            {
                val.LastAccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            
            var ct = val.UrlCostTime[path];
            _logger.Info("统计访问: 访问:{} 总次数:{} 总耗时:{}ms, 平均耗时: {}ms", url.TrimEnd('/') + path, ct.Count, ct.UseTime, ct.Average);
            return true;
        }

        SharedData.DestStas.DoLockKeyFunc(destUrl, (key) => new(), (val) => func(destUrl, val));
        var host = RequestUtil.GetRequestBaseUrl(context.Request);
        SharedData.ReqStas.DoLockKeyFunc(host, (key) => new(), (val) => func(host, val));
        var client_ip = RequestUtil.GetClientIp(context.Request);
        SharedData.ClientStas.DoLockKeyFunc(client_ip, (key) => new(), (val) => func(client_ip, val, updateLastAccess: true));
        
        // 记录流量统计
        var statusCode = context.Response.StatusCode;
        var isPageView = IsPageView(context.Request.Path);
        var visitorId = GetVisitorId(context);
        SharedData.Traffic.RecordRequest(client_ip, statusCode, isPageView, visitorId);

        // 记录地理位置流量统计
        try
        {
            var accessControlService = context.RequestServices.GetService<IAccessControlService>();
            var geoInfo = accessControlService?.GetGeoInfo(client_ip);
            if (geoInfo != null && !string.IsNullOrEmpty(geoInfo.Country))
            {
                SharedData.GeoTraffic.RecordVisit(geoInfo.Country, geoInfo.Region);
            }
        }
        catch { /* 地理统计为 best-effort，不影响请求处理 */ }

        // 白名单, 不进行后续的分析统计
        if(statisticService.IsWhitePath(path)) {
            return;
        }
        
        SharedData.NewClientVisits.Incr(client_ip, 1);
        SharedData.ClientDetailVisits.DoLockKeyFunc(client_ip, (key) => new(), (val) =>
        {
            val.AddLast(new ReqestShortMsg(context, path, costTime));
            return true;
        });
        
        // 记录 API 详细耗时统计
        RecordApiTiming(context, path, costTime);
    }
    
    /// <summary>
    /// 记录 API 详细耗时统计
    /// </summary>
    public static void RecordApiTiming(HttpContext context, string path, long totalTime)
    {
        var method = context.Request.Method;
        var statusCode = context.Response.StatusCode;
        
        // 获取后端耗时（如果有代理到后端的话）
        long backendTime = 0;
        if (context.Items.TryGetValue("BackendTime", out var backendTimeObj) && backendTimeObj is long bt)
        {
            backendTime = bt;
        }
        
        // 获取后端地址
        string backend = "";
        if (context.Items.TryGetValue("ProxyDestUrl", out var destUrlObj) && destUrlObj is string destUrl)
        {
            backend = destUrl;
        }
        
        // 获取原始请求的 Host（scheme + host）
        var originalHost = context.Request.Host.ToString();
        var scheme = context.Request.Scheme;
        var originalUrl = $"{scheme}://{originalHost}";
        
        // Key 包含后端地址，区分不同后端的统计
        var key = string.IsNullOrEmpty(backend) 
            ? $"{method}:{path}" 
            : $"{method}:{path}@{backend}";
        
        SharedData.ApiTimings.DoLockKeyFunc(key, 
            (k) => new ApiTimingStatistic 
            { 
                Path = path, 
                Method = method, 
                Backend = backend,
                OriginalHost = originalUrl
            },
            (val) =>
            {
                val.RecordRequest(totalTime, backendTime, statusCode);
                return true;
            });
    }

    public static int GetPeriodIdx(int period, DateTime? time = null) {
        if(time == null) {
            time = DateTime.UtcNow;
        }
        return (int)((time.Value - DateTime.UnixEpoch).TotalSeconds / period);
    }
    
    /// <summary>
    /// 记录拦截事件（WAF/CC/IP黑名单等）
    /// </summary>
    public static void RecordIntercept(HttpContext context, int statusCode, bool isAttack = false)
    {
        var clientIp = RequestUtil.GetClientIp(context.Request);
        SharedData.Traffic.RecordIntercept(clientIp, statusCode, isAttack);

        // 记录地理位置拦截统计
        try
        {
            var accessControlService = context.RequestServices.GetService<IAccessControlService>();
            var geoInfo = accessControlService?.GetGeoInfo(clientIp);
            if (geoInfo != null && !string.IsNullOrEmpty(geoInfo.Country))
            {
                SharedData.GeoTraffic.RecordIntercept(geoInfo.Country, geoInfo.Region);
            }
        }
        catch { /* best-effort */ }
    }
    
    /// <summary>
    /// 记录攻击IP（用于标记检测到的攻击者）
    /// </summary>
    public static void RecordAttackIp(string clientIp)
    {
        SharedData.Traffic.RecordIntercept(clientIp, 403, isAttack: true);
    }
    
    /// <summary>
    /// 判断是否为页面访问（非静态资源）
    /// </summary>
    private static bool IsPageView(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        
        var lower = path.ToLowerInvariant();
        // 排除静态资源
        var staticExtensions = new[] { ".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".woff", ".woff2", ".ttf", ".eot", ".map" };
        return !staticExtensions.Any(ext => lower.EndsWith(ext));
    }
    
    /// <summary>
    /// 获取访客标识（基于Cookie或其他方式）
    /// </summary>
    private static string? GetVisitorId(HttpContext context)
    {
        // 常见的 Session/Visitor Cookie 名称
        var sessionCookieNames = new[]
        {
            // LyWaf 自定义
            "_lywaf_vid",
            // ASP.NET
            "ASP.NET_SessionId", ".AspNetCore.Session",
            // PHP
            "PHPSESSID",
            // Java
            "JSESSIONID",
            // Python
            "session", "sessionid",
            // Node.js
            "connect.sid",
            // 通用
            "sid", "session_id", "visitor_id", "vid",
        };
        
        // 尝试从已知的 session cookie 中获取访客ID
        foreach (var cookieName in sessionCookieNames)
        {
            if (context.Request.Cookies.TryGetValue(cookieName, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        
        // 如果没有已知的 session cookie，尝试使用任意看起来像 session 的 cookie
        foreach (var cookie in context.Request.Cookies)
        {
            var name = cookie.Key.ToLowerInvariant();
            // 检查是否包含 session/sid/visitor 等关键词
            if (name.Contains("session") || name.Contains("sid") || name.Contains("visitor") || name.Contains("token"))
            {
                if (!string.IsNullOrEmpty(cookie.Value) && cookie.Value.Length >= 8)
                {
                    return cookie.Value;
                }
            }
        }
        
        // 如果没有任何可用的 cookie，返回 null（不计入 UV）
        return null;
    }
}