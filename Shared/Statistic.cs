using System.Security.Cryptography;

namespace LyWaf.Shared;

/// <summary>
/// 统计计数与耗时类
/// 用于记录请求次数和累计耗时，支持计算平均耗时
/// </summary>
public class StaCountTime : ICloneable
{
    /// <summary>
    /// 请求次数
    /// </summary>
    public int Count { get; set; } = 0;

    /// <summary>
    /// 累计耗时（毫秒）
    /// </summary>
    public long UseTime { get; set; } = 0;

    /// <summary>
    /// 增加一次请求记录
    /// </summary>
    /// <param name="time">本次请求耗时（毫秒）</param>
    public void IncrTime(long time)
    {
        Count++;
        UseTime += time;
    }

    /// <summary>
    /// 克隆当前对象
    /// </summary>
    public object Clone()
    {
        return MemberwiseClone();
    }

    /// <summary>
    /// 平均耗时（毫秒）
    /// 计算公式: 累计耗时 / 请求次数
    /// </summary>
    public double Average
    {
        get
        {
            return UseTime / Math.Max(Count, 1);
        }
    }
}

/// <summary>
/// IP/路径统计信息类
/// 用于记录某个IP或路径的访问统计，包含总体统计和各URL的详细统计
/// </summary>
public class IpStatistic : ICloneable
{
    /// <summary>
    /// 各URL路径的访问统计
    /// Key: 请求路径（如 /api/users）
    /// Value: 该路径的访问次数和耗时统计
    /// 示例: { "/api/config": { Count: 10, UseTime: 1000 } }
    /// </summary>
    public Dictionary<string, StaCountTime> UrlCostTime = [];

    /// <summary>
    /// 总体访问统计（所有URL的汇总）
    /// 包含总请求次数和总耗时
    /// </summary>
    public StaCountTime CountTime { get; set; } = new();

    /// <summary>
    /// 最后访问时间（Unix时间戳，毫秒）
    /// 用于记录客户端的最后一次访问时间
    /// </summary>
    public long LastAccessTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// 克隆当前对象（深拷贝）
    /// </summary>
    public object Clone()
    {
        return new IpStatistic
        {
            CountTime = (StaCountTime)CountTime.Clone(),
            UrlCostTime = new Dictionary<string, StaCountTime>(UrlCostTime),
            LastAccessTime = LastAccessTime,
        };
    }
}

/// <summary>
/// 客户端统计信息类
/// 用于记录单个客户端的访问行为统计
/// </summary>
public class ClientStatistic
{
    /// <summary>
    /// 各URL路径的访问次数
    /// Key: 请求路径（如 /api/users）
    /// Value: 访问次数
    /// 示例: { "/api/config": 10, "/api/users": 5 }
    /// </summary>
    public Dictionary<string, int> UrlVisitTimes = [];

    /// <summary>
    /// 总体访问统计
    /// 包含总请求次数和总耗时
    /// </summary>
    public StaCountTime CountTime { get; set; } = new();
}

/// <summary>
/// 请求简要信息类
/// 用于记录单次请求的关键信息，用于详细访问记录
/// </summary>
/// <param name="context">HTTP上下文，用于获取响应状态码</param>
/// <param name="path">请求路径</param>
/// <param name="costTime">请求耗时（毫秒）</param>
public class ReqestShortMsg(HttpContext context, string path, long costTime)
{
    /// <summary>
    /// 请求路径（如 /api/users）
    /// </summary>
    public string Path { get; set; } = path;

    /// <summary>
    /// 请求耗时（毫秒）
    /// </summary>
    public long CostTime { get; set; } = costTime;

    /// <summary>
    /// 请求时间（UTC）
    /// </summary>
    public DateTime ReqTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// HTTP响应状态码（如 200, 404, 500）
    /// </summary>
    public int StatusCode { get; set; } = context.Response.StatusCode;
}

/// <summary>
/// 流量统计类
/// 记录请求次数、独立IP、拦截次数、错误统计等
/// </summary>
public class TrafficStatistic
{
    private readonly object _lock = new();

    // 恢复基线值（从 DuckDB 恢复时设置，HashSet 无法恢复具体元素，只记录计数基线）
    private int _baseUniqueVisitors;
    private int _baseUniqueIps;
    private int _baseAttackIps;

    /// <summary>
    /// 总请求次数
    /// </summary>
    public long TotalRequests { get; private set; } = 0;
    
    /// <summary>
    /// 页面访问次数 (PV) - 非静态资源请求
    /// </summary>
    public long PageViews { get; private set; } = 0;
    
    /// <summary>
    /// 独立访客数 (UV) - 基于Cookie或其他标识
    /// </summary>
    public HashSet<string> UniqueVisitors { get; } = [];
    
    /// <summary>
    /// 独立IP数
    /// </summary>
    public HashSet<string> UniqueIps { get; } = [];
    
    /// <summary>
    /// 拦截次数 (WAF/CC/IP黑名单等)
    /// </summary>
    public long InterceptCount { get; private set; } = 0;
    
    /// <summary>
    /// 攻击IP集合
    /// </summary>
    public HashSet<string> AttackIps { get; } = [];
    
    /// <summary>
    /// 4xx 错误数
    /// </summary>
    public long Error4xxCount { get; private set; } = 0;
    
    /// <summary>
    /// 4xx 拦截数 (WAF主动拦截导致的4xx)
    /// </summary>
    public long Intercept4xxCount { get; private set; } = 0;
    
    /// <summary>
    /// 5xx 错误数
    /// </summary>
    public long Error5xxCount { get; private set; } = 0;
    
    /// <summary>
    /// 统计开始时间
    /// </summary>
    public DateTime StartTime { get; private set; } = DateTime.UtcNow;
    
    /// <summary>
    /// 记录一次请求
    /// </summary>
    /// <param name="clientIp">客户端IP</param>
    /// <param name="statusCode">响应状态码</param>
    /// <param name="isPageView">是否为页面访问</param>
    /// <param name="visitorId">访客标识(可选)</param>
    /// <param name="isIntercept">是否为主动拦截</param>
    /// <param name="isAttack">是否为攻击请求</param>
    public void RecordRequest(string clientIp, int statusCode, bool isPageView = false, 
        string? visitorId = null, bool isIntercept = false, bool isAttack = false)
    {
        lock (_lock)
        {
            TotalRequests++;
            
            if (isPageView)
                PageViews++;
            
            UniqueIps.Add(clientIp);
            
            if (!string.IsNullOrEmpty(visitorId))
                UniqueVisitors.Add(visitorId);
            
            if (isIntercept)
            {
                InterceptCount++;
                if (statusCode >= 400 && statusCode < 500)
                    Intercept4xxCount++;
            }
            
            if (isAttack)
                AttackIps.Add(clientIp);
            
            if (statusCode >= 400 && statusCode < 500)
                Error4xxCount++;
            else if (statusCode >= 500)
                Error5xxCount++;
        }
    }
    
    /// <summary>
    /// 记录一次拦截
    /// </summary>
    public void RecordIntercept(string clientIp, int statusCode, bool isAttack = false)
    {
        lock (_lock)
        {
            InterceptCount++;
            UniqueIps.Add(clientIp);
            
            if (isAttack)
                AttackIps.Add(clientIp);
            
            if (statusCode >= 400 && statusCode < 500)
            {
                Error4xxCount++;
                Intercept4xxCount++;
            }
            else if (statusCode >= 500)
            {
                Error5xxCount++;
            }
        }
    }
    
    /// <summary>
    /// 4xx 错误率
    /// </summary>
    public double Error4xxRate => TotalRequests > 0 ? (double)Error4xxCount / TotalRequests * 100 : 0;
    
    /// <summary>
    /// 4xx 拦截率 (拦截数占总4xx的比率)
    /// </summary>
    public double Intercept4xxRate => Error4xxCount > 0 ? (double)Intercept4xxCount / Error4xxCount * 100 : 0;
    
    /// <summary>
    /// 5xx 错误率
    /// </summary>
    public double Error5xxRate => TotalRequests > 0 ? (double)Error5xxCount / TotalRequests * 100 : 0;
    
    /// <summary>
    /// 重置统计
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            TotalRequests = 0;
            PageViews = 0;
            UniqueVisitors.Clear();
            UniqueIps.Clear();
            InterceptCount = 0;
            AttackIps.Clear();
            Error4xxCount = 0;
            Intercept4xxCount = 0;
            Error5xxCount = 0;
            StartTime = DateTime.UtcNow;
        }
    }
    
    /// <summary>
    /// 记录攻击IP（仅添加到攻击IP集合，不增加请求计数）
    /// 用于 DoFbIp 等封禁操作时记录攻击来源
    /// </summary>
    public void RecordAttackIp(string clientIp)
    {
        lock (_lock)
        {
            AttackIps.Add(clientIp);
        }
    }
    
    /// <summary>
    /// 获取快照（线程安全）
    /// </summary>
    public TrafficStatisticSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new TrafficStatisticSnapshot
            {
                TotalRequests = TotalRequests,
                PageViews = PageViews,
                UniqueVisitors = _baseUniqueVisitors + UniqueVisitors.Count,
                UniqueIps = _baseUniqueIps + UniqueIps.Count,
                InterceptCount = InterceptCount,
                AttackIps = _baseAttackIps + AttackIps.Count,
                Error4xxCount = Error4xxCount,
                Intercept4xxCount = Intercept4xxCount,
                Error5xxCount = Error5xxCount,
                Error4xxRate = Error4xxRate,
                Intercept4xxRate = Intercept4xxRate,
                Error5xxRate = Error5xxRate,
                StartTime = StartTime
            };
        }
    }

    /// <summary>
    /// 从 DuckDB 恢复累计数据（启动时调用）
    /// </summary>
    public void RestoreFrom(long totalRequests, long pageViews, int uniqueVisitors, int uniqueIps,
        long interceptCount, int attackIps, long error4xx, long intercept4xx, long error5xx, DateTime startTime)
    {
        lock (_lock)
        {
            TotalRequests = totalRequests;
            PageViews = pageViews;
            _baseUniqueVisitors = uniqueVisitors;
            _baseUniqueIps = uniqueIps;
            InterceptCount = interceptCount;
            _baseAttackIps = attackIps;
            Error4xxCount = error4xx;
            Intercept4xxCount = intercept4xx;
            Error5xxCount = error5xx;
            StartTime = startTime;
        }
    }
}

/// <summary>
/// 流量统计快照（不可变）
/// </summary>
public class TrafficStatisticSnapshot
{
    public long TotalRequests { get; init; }
    public long PageViews { get; init; }
    public int UniqueVisitors { get; init; }
    public int UniqueIps { get; init; }
    public long InterceptCount { get; init; }
    public int AttackIps { get; init; }
    public long Error4xxCount { get; init; }
    public long Intercept4xxCount { get; init; }
    public long Error5xxCount { get; init; }
    public double Error4xxRate { get; init; }
    public double Intercept4xxRate { get; init; }
    public double Error5xxRate { get; init; }
    public DateTime StartTime { get; init; }
}

/// <summary>
/// API 耗时详细统计
/// 包含总耗时（客户端→网关→客户端）和后端耗时（网关→后端→网关）
/// </summary>
public class ApiTimingStatistic : ICloneable
{
    /// <summary>
    /// API 路径（如 /api/users）
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// HTTP 方法（GET, POST 等）
    /// </summary>
    public string Method { get; set; } = "";

    /// <summary>
    /// 后端地址（如 http://backend1:8080）
    /// 用于区分不同后端的稳定性
    /// </summary>
    public string Backend { get; set; } = "";

    /// <summary>
    /// 请求总次数
    /// </summary>
    public int RequestCount { get; set; } = 0;

    /// <summary>
    /// 总耗时统计（客户端→网关→客户端的完整链路）
    /// 累计耗时（毫秒）
    /// </summary>
    public long TotalTime { get; set; } = 0;

    /// <summary>
    /// 后端耗时统计（网关→后端→网关）
    /// 累计耗时（毫秒）
    /// </summary>
    public long BackendTime { get; set; } = 0;

    /// <summary>
    /// 最小总耗时（毫秒）
    /// </summary>
    public long MinTotalTime { get; set; } = long.MaxValue;

    /// <summary>
    /// 最大总耗时（毫秒）
    /// </summary>
    public long MaxTotalTime { get; set; } = 0;

    /// <summary>
    /// 最小后端耗时（毫秒）
    /// </summary>
    public long MinBackendTime { get; set; } = long.MaxValue;

    /// <summary>
    /// 最大后端耗时（毫秒）
    /// </summary>
    public long MaxBackendTime { get; set; } = 0;

    /// <summary>
    /// 最后请求时间
    /// </summary>
    public DateTime LastRequestTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 原始请求的 Host（如 localhost:5000）
    /// 用于在控制台点击时回到原始请求的 URL
    /// </summary>
    public string OriginalHost { get; set; } = "";

    /// <summary>
    /// 各状态码计数
    /// </summary>
    public Dictionary<int, int> StatusCodeCounts { get; set; } = [];

    /// <summary>
    /// 平均总耗时（毫秒）
    /// </summary>
    public double AvgTotalTime => RequestCount > 0 ? (double)TotalTime / RequestCount : 0;

    /// <summary>
    /// 平均后端耗时（毫秒）
    /// </summary>
    public double AvgBackendTime => RequestCount > 0 ? (double)BackendTime / RequestCount : 0;

    /// <summary>
    /// 平均网关处理耗时（毫秒）= 总耗时 - 后端耗时
    /// </summary>
    public double AvgGatewayTime => AvgTotalTime - AvgBackendTime;

    /// <summary>
    /// 记录一次请求
    /// </summary>
    public void RecordRequest(long totalTime, long backendTime, int statusCode)
    {
        RequestCount++;
        TotalTime += totalTime;
        BackendTime += backendTime;

        if (totalTime < MinTotalTime) MinTotalTime = totalTime;
        if (totalTime > MaxTotalTime) MaxTotalTime = totalTime;
        if (backendTime < MinBackendTime) MinBackendTime = backendTime;
        if (backendTime > MaxBackendTime) MaxBackendTime = backendTime;

        LastRequestTime = DateTime.UtcNow;

        if (StatusCodeCounts.TryGetValue(statusCode, out var count))
            StatusCodeCounts[statusCode] = count + 1;
        else
            StatusCodeCounts[statusCode] = 1;
    }

    public object Clone()
    {
        return new ApiTimingStatistic
        {
            Path = Path,
            Method = Method,
            Backend = Backend,
            OriginalHost = OriginalHost,
            RequestCount = RequestCount,
            TotalTime = TotalTime,
            BackendTime = BackendTime,
            MinTotalTime = MinTotalTime,
            MaxTotalTime = MaxTotalTime,
            MinBackendTime = MinBackendTime,
            MaxBackendTime = MaxBackendTime,
            LastRequestTime = LastRequestTime,
            StatusCodeCounts = new Dictionary<int, int>(StatusCodeCounts)
        };
    }
}

/// <summary>
/// 安全事件类型枚举
/// </summary>
public enum SecurityEventType
{
    /// <summary>WAF攻击拦截（SQL注入、XSS等）</summary>
    WafIntercept,
    /// <summary>黑名单拦截</summary>
    BlacklistBlock,
    /// <summary>CC攻击拦截</summary>
    CcAttack,
    /// <summary>爬虫检测</summary>
    CrawlerDetect,
    /// <summary>地理位置拦截</summary>
    GeoBlock,
    /// <summary>频率限制</summary>
    RateLimit,
    /// <summary>异常行为</summary>
    AbnormalBehavior
}

/// <summary>
/// 安全态势时间片数据
/// </summary>
public class SecurityTimeSlot
{
    public DateTime Time { get; set; }
    public long WafIntercept { get; set; }
    public long BlacklistBlock { get; set; }
    public long CcAttack { get; set; }
    public long CrawlerDetect { get; set; }
    public long GeoBlock { get; set; }
    public long RateLimit { get; set; }
    public long Total => WafIntercept + BlacklistBlock + CcAttack + CrawlerDetect + GeoBlock + RateLimit;
}

/// <summary>
/// 攻击源IP统计
/// </summary>
public class AttackSourceStat
{
    public string Ip { get; set; } = "";
    public long Count { get; set; }
    public SecurityEventType LastEventType { get; set; }
    public DateTime LastAttackTime { get; set; }
}

/// <summary>
/// 安全态势统计类
/// 记录各类安全事件的历史数据和攻击源统计
/// </summary>
public class SecurityStatistic
{
    private readonly object _lock = new();
    private const int MaxTimeSlots = 288; // 保留24小时数据（每5分钟一个点）
    private const int TimeSlotMinutes = 5;
    
    /// <summary>统计开始时间</summary>
    public DateTime StartTime { get; private set; } = DateTime.UtcNow;
    
    // 各类安全事件计数
    public long WafInterceptCount { get; private set; }
    public long BlacklistBlockCount { get; private set; }
    public long CcAttackCount { get; private set; }
    public long CrawlerDetectCount { get; private set; }
    public long GeoBlockCount { get; private set; }
    public long RateLimitCount { get; private set; }
    
    /// <summary>历史时间片数据</summary>
    private readonly LinkedList<SecurityTimeSlot> _timeSlots = new();
    private SecurityTimeSlot? _currentSlot;
    private DateTime _currentSlotTime;
    
    /// <summary>攻击源IP统计 (IP -> 攻击次数)</summary>
    private readonly Dictionary<string, AttackSourceStat> _attackSources = new();
    
    /// <summary>各类型攻击源IP统计</summary>
    private readonly Dictionary<SecurityEventType, Dictionary<string, long>> _typedAttackSources = new();
    
    public SecurityStatistic()
    {
        foreach (SecurityEventType type in Enum.GetValues<SecurityEventType>())
        {
            _typedAttackSources[type] = new Dictionary<string, long>();
        }
    }
    
    /// <summary>
    /// 获取当前时间片
    /// </summary>
    private SecurityTimeSlot GetOrCreateCurrentSlot()
    {
        var now = DateTime.UtcNow;
        var slotTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, 
            (now.Minute / TimeSlotMinutes) * TimeSlotMinutes, 0, DateTimeKind.Utc);
        
        if (_currentSlot == null || slotTime > _currentSlotTime)
        {
            // 保存旧的时间片
            if (_currentSlot != null)
            {
                _timeSlots.AddLast(_currentSlot);
                // 保持最大数量
                while (_timeSlots.Count > MaxTimeSlots)
                {
                    _timeSlots.RemoveFirst();
                }
            }
            
            // 创建新时间片
            _currentSlot = new SecurityTimeSlot { Time = slotTime };
            _currentSlotTime = slotTime;
        }
        
        return _currentSlot;
    }
    
    /// <summary>
    /// 记录安全事件
    /// </summary>
    public void RecordEvent(SecurityEventType eventType, string clientIp)
    {
        lock (_lock)
        {
            var slot = GetOrCreateCurrentSlot();
            
            // 更新计数
            switch (eventType)
            {
                case SecurityEventType.WafIntercept:
                    WafInterceptCount++;
                    slot.WafIntercept++;
                    break;
                case SecurityEventType.BlacklistBlock:
                    BlacklistBlockCount++;
                    slot.BlacklistBlock++;
                    break;
                case SecurityEventType.CcAttack:
                    CcAttackCount++;
                    slot.CcAttack++;
                    break;
                case SecurityEventType.CrawlerDetect:
                    CrawlerDetectCount++;
                    slot.CrawlerDetect++;
                    break;
                case SecurityEventType.GeoBlock:
                    GeoBlockCount++;
                    slot.GeoBlock++;
                    break;
                case SecurityEventType.RateLimit:
                case SecurityEventType.AbnormalBehavior:
                    RateLimitCount++;
                    slot.RateLimit++;
                    break;
            }
            
            // 更新攻击源统计
            if (!_attackSources.TryGetValue(clientIp, out var stat))
            {
                stat = new AttackSourceStat { Ip = clientIp };
                _attackSources[clientIp] = stat;
            }
            stat.Count++;
            stat.LastEventType = eventType;
            stat.LastAttackTime = DateTime.UtcNow;
            
            // 更新分类攻击源统计
            if (!_typedAttackSources[eventType].TryGetValue(clientIp, out var count))
            {
                count = 0;
            }
            _typedAttackSources[eventType][clientIp] = count + 1;
        }
    }
    
    /// <summary>
    /// 获取历史时间片数据
    /// </summary>
    public List<SecurityTimeSlot> GetTimeSlots(int hours = 24)
    {
        lock (_lock)
        {
            var result = new List<SecurityTimeSlot>();
            var cutoff = DateTime.UtcNow.AddHours(-hours);
            
            foreach (var slot in _timeSlots)
            {
                if (slot.Time >= cutoff)
                {
                    result.Add(slot);
                }
            }
            
            // 添加当前时间片
            if (_currentSlot != null && _currentSlot.Time >= cutoff)
            {
                result.Add(_currentSlot);
            }
            
            return result;
        }
    }
    
    /// <summary>
    /// 获取攻击源Top N
    /// </summary>
    public List<AttackSourceStat> GetTopAttackSources(int topN = 10)
    {
        lock (_lock)
        {
            return _attackSources.Values
                .OrderByDescending(x => x.Count)
                .Take(topN)
                .ToList();
        }
    }
    
    /// <summary>
    /// 获取指定类型的攻击源Top N
    /// </summary>
    public List<(string Ip, long Count)> GetTopAttackSourcesByType(SecurityEventType eventType, int topN = 5)
    {
        lock (_lock)
        {
            if (!_typedAttackSources.TryGetValue(eventType, out var sources))
                return new List<(string, long)>();

            return sources
                .OrderByDescending(x => x.Value)
                .Take(topN)
                .Select(x => (x.Key, x.Value))
                .ToList();
        }
    }

    /// <summary>
    /// 获取所有分类攻击源数据（用于持久化刷写）
    /// </summary>
    public List<(SecurityEventType EventType, string Ip, long Count)> GetAllTypedAttackSources(int maxPerType = 200)
    {
        lock (_lock)
        {
            var result = new List<(SecurityEventType, string, long)>();
            foreach (var (eventType, sources) in _typedAttackSources)
            {
                foreach (var (ip, count) in sources.OrderByDescending(x => x.Value).Take(maxPerType))
                {
                    result.Add((eventType, ip, count));
                }
            }
            return result;
        }
    }
    
    /// <summary>
    /// 获取统计快照
    /// </summary>
    public SecurityStatisticSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new SecurityStatisticSnapshot
            {
                StartTime = StartTime,
                WafInterceptCount = WafInterceptCount,
                BlacklistBlockCount = BlacklistBlockCount,
                CcAttackCount = CcAttackCount,
                CrawlerDetectCount = CrawlerDetectCount,
                GeoBlockCount = GeoBlockCount,
                RateLimitCount = RateLimitCount,
                TotalInterceptCount = WafInterceptCount + BlacklistBlockCount + CcAttackCount + 
                    CrawlerDetectCount + GeoBlockCount + RateLimitCount,
                UniqueAttackIps = _attackSources.Count
            };
        }
    }
    
    /// <summary>
    /// 重置统计
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            StartTime = DateTime.UtcNow;
            WafInterceptCount = 0;
            BlacklistBlockCount = 0;
            CcAttackCount = 0;
            CrawlerDetectCount = 0;
            GeoBlockCount = 0;
            RateLimitCount = 0;
            _timeSlots.Clear();
            _currentSlot = null;
            _attackSources.Clear();
            foreach (var dict in _typedAttackSources.Values)
            {
                dict.Clear();
            }
        }
    }

    /// <summary>
    /// 从 DuckDB 恢复累计计数（启动时调用）
    /// </summary>
    public void RestoreCounters(DateTime startTime, long wafIntercept, long blacklistBlock,
        long ccAttack, long crawlerDetect, long geoBlock, long rateLimit)
    {
        lock (_lock)
        {
            StartTime = startTime;
            WafInterceptCount = wafIntercept;
            BlacklistBlockCount = blacklistBlock;
            CcAttackCount = ccAttack;
            CrawlerDetectCount = crawlerDetect;
            GeoBlockCount = geoBlock;
            RateLimitCount = rateLimit;
        }
    }

    /// <summary>
    /// 从 DuckDB 恢复时间片历史（启动时调用）
    /// </summary>
    public void RestoreTimeSlots(List<SecurityTimeSlot> slots)
    {
        lock (_lock)
        {
            _timeSlots.Clear();
            _currentSlot = null;
            foreach (var slot in slots)
            {
                _timeSlots.AddLast(slot);
            }
            // 保持最大数量
            while (_timeSlots.Count > MaxTimeSlots)
            {
                _timeSlots.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// 从 DuckDB 恢复攻击源统计（启动时调用）
    /// </summary>
    public void RestoreAttackSources(List<AttackSourceStat> sources)
    {
        lock (_lock)
        {
            _attackSources.Clear();
            foreach (var source in sources)
            {
                _attackSources[source.Ip] = source;
            }
        }
    }

    /// <summary>
    /// 从 DuckDB 恢复分类攻击源统计（启动时调用）
    /// </summary>
    public void RestoreTypedAttackSources(List<(SecurityEventType EventType, string Ip, long Count)> typedSources)
    {
        lock (_lock)
        {
            foreach (var dict in _typedAttackSources.Values)
            {
                dict.Clear();
            }
            foreach (var (eventType, ip, count) in typedSources)
            {
                if (_typedAttackSources.TryGetValue(eventType, out var sources))
                {
                    sources[ip] = count;
                }
            }
        }
    }
}

/// <summary>
/// 安全态势统计快照
/// </summary>
public class SecurityStatisticSnapshot
{
    public DateTime StartTime { get; init; }
    public long WafInterceptCount { get; init; }
    public long BlacklistBlockCount { get; init; }
    public long CcAttackCount { get; init; }
    public long CrawlerDetectCount { get; init; }
    public long GeoBlockCount { get; init; }
    public long RateLimitCount { get; init; }
    public long TotalInterceptCount { get; init; }
    public int UniqueAttackIps { get; init; }
}

/// <summary>
/// 地理位置流量统计
/// 记录按国家和省份维度的访问量和拦截量，用于地图可视化
/// </summary>
public class GeoTrafficStatistic
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _countryVisits = new();
    private readonly Dictionary<string, long> _countryIntercepts = new();
    private readonly Dictionary<string, long> _regionVisits = new();
    private readonly Dictionary<string, long> _regionIntercepts = new();

    /// <summary>
    /// 记录一次访问
    /// </summary>
    public void RecordVisit(string country, string region)
    {
        if (string.IsNullOrEmpty(country)) return;
        lock (_lock)
        {
            _countryVisits[country] = _countryVisits.GetValueOrDefault(country) + 1;
            if (!string.IsNullOrEmpty(region))
            {
                _regionVisits[region] = _regionVisits.GetValueOrDefault(region) + 1;
            }
        }
    }

    /// <summary>
    /// 记录一次拦截
    /// </summary>
    public void RecordIntercept(string country, string region)
    {
        if (string.IsNullOrEmpty(country)) return;
        lock (_lock)
        {
            _countryIntercepts[country] = _countryIntercepts.GetValueOrDefault(country) + 1;
            if (!string.IsNullOrEmpty(region))
            {
                _regionIntercepts[region] = _regionIntercepts.GetValueOrDefault(region) + 1;
            }
        }
    }

    /// <summary>
    /// 获取快照
    /// </summary>
    public GeoTrafficSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new GeoTrafficSnapshot
            {
                CountryVisits = new Dictionary<string, long>(_countryVisits),
                CountryIntercepts = new Dictionary<string, long>(_countryIntercepts),
                RegionVisits = new Dictionary<string, long>(_regionVisits),
                RegionIntercepts = new Dictionary<string, long>(_regionIntercepts),
            };
        }
    }

    /// <summary>
    /// 重置统计
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _countryVisits.Clear();
            _countryIntercepts.Clear();
            _regionVisits.Clear();
            _regionIntercepts.Clear();
        }
    }

    /// <summary>
    /// 从 DuckDB 恢复地理数据（启动时调用）
    /// </summary>
    public void RestoreFrom(GeoTrafficSnapshot snapshot)
    {
        lock (_lock)
        {
            _countryVisits.Clear();
            _countryIntercepts.Clear();
            _regionVisits.Clear();
            _regionIntercepts.Clear();

            foreach (var (k, v) in snapshot.CountryVisits) _countryVisits[k] = v;
            foreach (var (k, v) in snapshot.CountryIntercepts) _countryIntercepts[k] = v;
            foreach (var (k, v) in snapshot.RegionVisits) _regionVisits[k] = v;
            foreach (var (k, v) in snapshot.RegionIntercepts) _regionIntercepts[k] = v;
        }
    }
}

/// <summary>
/// 地理位置流量快照
/// </summary>
public class GeoTrafficSnapshot
{
    public Dictionary<string, long> CountryVisits { get; init; } = new();
    public Dictionary<string, long> CountryIntercepts { get; init; } = new();
    public Dictionary<string, long> RegionVisits { get; init; } = new();
    public Dictionary<string, long> RegionIntercepts { get; init; } = new();
}

/// <summary>
/// QPS 统计追踪器
/// 使用环形缓冲区记录每秒请求数，支持按不同粒度（5秒/1分钟/1小时）查询
/// </summary>
public class QpsTracker
{
    // 每秒一个槽位，保留最近 3700 秒（略超1小时，便于对齐）
    private const int BucketCount = 3700;
    private readonly long[] _buckets = new long[BucketCount];
    private readonly object _lock = new();
    private long _currentSecond;
    private long _totalSinceLastFlush;

    public QpsTracker()
    {
        _currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// 获取自上次调用以来的请求总数（用于 DuckDB flush）
    /// </summary>
    public long FlushCount()
    {
        lock (_lock)
        {
            var count = _totalSinceLastFlush;
            _totalSinceLastFlush = 0;
            return count;
        }
    }

    /// <summary>
    /// 记录一次请求（在请求处理时调用）
    /// </summary>
    public void Record()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            Advance(now);
            _buckets[now % BucketCount]++;
            _totalSinceLastFlush++;
        }
    }

    /// <summary>
    /// 获取历史 QPS 数据
    /// </summary>
    /// <param name="granularity">粒度：5s / 1min / 1hour</param>
    public List<QpsDataPoint> GetHistory(string granularity)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            Advance(now);
        }

        int stepSeconds, totalPoints;
        switch (granularity)
        {
            case "5s":
                stepSeconds = 5;
                totalPoints = 120; // 10 分钟
                break;
            case "1min":
                stepSeconds = 60;
                totalPoints = 60; // 1 小时
                break;
            case "1hour":
                stepSeconds = 300; // 5 分钟一个点
                totalPoints = 12;  // 最近 1 小时
                break;
            default:
                stepSeconds = 5;
                totalPoints = 120;
                break;
        }

        var result = new List<QpsDataPoint>(totalPoints);
        // 从最近的完整 step 开始往前推
        var endSecond = now - (now % stepSeconds);
        var startSecond = endSecond - (long)(totalPoints - 1) * stepSeconds;

        lock (_lock)
        {
            for (int i = 0; i < totalPoints; i++)
            {
                var bucketStart = startSecond + (long)i * stepSeconds;
                long sum = 0;
                for (int s = 0; s < stepSeconds && s < BucketCount; s++)
                {
                    var sec = bucketStart + s;
                    if (sec >= now - BucketCount + 1 && sec <= now)
                        sum += _buckets[sec % BucketCount];
                }
                var qps = (double)sum / stepSeconds;
                var time = DateTimeOffset.FromUnixTimeSeconds(bucketStart).ToLocalTime();
                result.Add(new QpsDataPoint
                {
                    Time = time.ToString("HH:mm:ss"),
                    Qps = Math.Round(qps, 2),
                    RequestCount = sum
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 推进到当前秒，清零中间跳过的槽位
    /// </summary>
    private void Advance(long nowSecond)
    {
        if (nowSecond <= _currentSecond) return;
        var gap = Math.Min(nowSecond - _currentSecond, BucketCount);
        for (long s = _currentSecond + 1; s <= _currentSecond + gap; s++)
            _buckets[s % BucketCount] = 0;
        _currentSecond = nowSecond;
    }
}

public class QpsDataPoint
{
    public string Time { get; set; } = "";
    public double Qps { get; set; }
    public long RequestCount { get; set; }
}

/// <summary>
/// 带宽追踪器：环形缓冲区记录每秒入站/出站字节
/// </summary>
public class BandwidthTracker
{
    private const int BucketCount = 3700;
    private readonly long[] _inbound = new long[BucketCount];
    private readonly long[] _outbound = new long[BucketCount];
    private readonly object _lock = new();
    private long _currentSecond;
    private long _inboundSinceLastFlush;
    private long _outboundSinceLastFlush;

    public BandwidthTracker()
    {
        _currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// 记录一次请求的入站/出站字节
    /// </summary>
    public void Record(long inboundBytes, long outboundBytes)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            Advance(now);
            _inbound[now % BucketCount] += inboundBytes;
            _outbound[now % BucketCount] += outboundBytes;
            _inboundSinceLastFlush += inboundBytes;
            _outboundSinceLastFlush += outboundBytes;
        }
    }

    /// <summary>
    /// 获取并重置自上次刷写以来的累计字节（用于 DuckDB flush）
    /// </summary>
    public (long Inbound, long Outbound) FlushCount()
    {
        lock (_lock)
        {
            var inb = _inboundSinceLastFlush;
            var outb = _outboundSinceLastFlush;
            _inboundSinceLastFlush = 0;
            _outboundSinceLastFlush = 0;
            return (inb, outb);
        }
    }

    /// <summary>
    /// 获取历史带宽数据
    /// </summary>
    public List<BandwidthDataPoint> GetHistory(string granularity)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            Advance(now);
        }

        int stepSeconds, totalPoints;
        switch (granularity)
        {
            case "5s":
                stepSeconds = 5; totalPoints = 120;
                break;
            case "1min":
                stepSeconds = 60; totalPoints = 60;
                break;
            case "1hour":
                stepSeconds = 300; totalPoints = 12;
                break;
            default:
                stepSeconds = 5; totalPoints = 120;
                break;
        }

        var result = new List<BandwidthDataPoint>(totalPoints);
        var endSecond = now - (now % stepSeconds);
        var startSecond = endSecond - (long)(totalPoints - 1) * stepSeconds;

        lock (_lock)
        {
            for (int i = 0; i < totalPoints; i++)
            {
                var bucketStart = startSecond + (long)i * stepSeconds;
                long inSum = 0, outSum = 0;
                for (int s = 0; s < stepSeconds && s < BucketCount; s++)
                {
                    var sec = bucketStart + s;
                    if (sec >= now - BucketCount + 1 && sec <= now)
                    {
                        inSum += _inbound[sec % BucketCount];
                        outSum += _outbound[sec % BucketCount];
                    }
                }
                var time = DateTimeOffset.FromUnixTimeSeconds(bucketStart).ToLocalTime();
                result.Add(new BandwidthDataPoint
                {
                    Time = time.ToString("HH:mm:ss"),
                    InboundBytes = inSum,
                    OutboundBytes = outSum,
                    InboundRate = Math.Round((double)inSum / stepSeconds, 2),
                    OutboundRate = Math.Round((double)outSum / stepSeconds, 2),
                });
            }
        }

        return result;
    }

    private void Advance(long nowSecond)
    {
        if (nowSecond <= _currentSecond) return;
        var gap = Math.Min(nowSecond - _currentSecond, BucketCount);
        for (long s = _currentSecond + 1; s <= _currentSecond + gap; s++)
        {
            _inbound[s % BucketCount] = 0;
            _outbound[s % BucketCount] = 0;
        }
        _currentSecond = nowSecond;
    }
}

public class BandwidthDataPoint
{
    public string Time { get; set; } = "";
    /// <summary>入站总字节</summary>
    public long InboundBytes { get; set; }
    /// <summary>出站总字节</summary>
    public long OutboundBytes { get; set; }
    /// <summary>入站速率 bytes/s</summary>
    public double InboundRate { get; set; }
    /// <summary>出站速率 bytes/s</summary>
    public double OutboundRate { get; set; }
}
