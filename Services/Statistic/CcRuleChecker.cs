using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LyWaf.Shared;
using LyWaf.Utils;
using NLog;

namespace LyWaf.Services.Statistic;

/// <summary>
/// CC 规则检查结果
/// </summary>
public class CcCheckResult
{
    public bool IsTriggered { get; set; }
    public AdvancedCcRule? TriggeredRule { get; set; }
    public CcAction Action { get; set; }
    public int ActionSeconds { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// CC 规则检查服务
/// </summary>
public interface ICcRuleChecker
{
    /// <summary>
    /// 检查请求是否触发 CC 规则
    /// </summary>
    CcCheckResult CheckRequest(HttpContext context, string clientIp);
    
    /// <summary>
    /// 记录 WAF 攻击事件（用于 FrequentAttack 类型）
    /// </summary>
    void RecordAttackEvent(string clientIp);
    
    /// <summary>
    /// 记录错误事件（用于 FrequentError 类型）
    /// </summary>
    void RecordErrorEvent(string clientIp, int statusCode);
    
    /// <summary>
    /// 执行 CC 处罚动作
    /// </summary>
    Task ExecuteAction(HttpContext context, CcCheckResult result, string clientIp);
}

public class CcRuleChecker : ICcRuleChecker
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IStatisticService _statisticService;
    
    // CC 计数器存储: Key = "{ruleId}:{clientIp}", Value = 请求计数信息
    private static readonly ConcurrentDictionary<string, CcCounter> _counters = new();
    
    // 攻击事件计数: Key = "{clientIp}", Value = 攻击计数
    private static readonly ConcurrentDictionary<string, CcCounter> _attackCounters = new();
    
    // 错误事件计数: Key = "{clientIp}", Value = 错误计数
    private static readonly ConcurrentDictionary<string, CcCounter> _errorCounters = new();
    
    // 已触发规则的 IP（避免重复处罚）: Key = "{ruleId}:{clientIp}"
    private static readonly ConcurrentDictionary<string, DateTime> _triggeredIps = new();
    
    // 编译后的正则表达式缓存
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    
    public CcRuleChecker(IStatisticService statisticService)
    {
        _statisticService = statisticService;
        
        // 启动清理任务
        _ = Task.Run(CleanupLoop);
    }
    
    /// <summary>
    /// 检查请求是否触发 CC 规则
    /// </summary>
    public CcCheckResult CheckRequest(HttpContext context, string clientIp)
    {
        var result = new CcCheckResult { IsTriggered = false };
        var rules = _statisticService.GetAdvancedCcRules()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Priority)
            .ToList();
        
        foreach (var rule in rules)
        {
            // 检查是否已经在处罚期内
            var triggeredKey = $"{rule.Id}:{clientIp}";
            if (_triggeredIps.TryGetValue(triggeredKey, out var triggeredTime))
            {
                if (DateTime.UtcNow < triggeredTime)
                {
                    // 仍在处罚期内
                    result.IsTriggered = true;
                    result.TriggeredRule = rule;
                    result.Action = rule.Action;
                    result.ActionSeconds = rule.ActionSeconds;
                    result.Message = $"IP {clientIp} 仍在 CC 规则 [{rule.Name}] 处罚期内";
                    return result;
                }
                else
                {
                    // 处罚期已过，移除
                    _triggeredIps.TryRemove(triggeredKey, out _);
                }
            }
            
            // 根据规则类型检查
            bool conditionsMatch = rule.Type switch
            {
                CcRuleType.FrequentAccess => MatchConditions(context, rule.Conditions, clientIp),
                CcRuleType.FrequentAttack => MatchConditions(context, rule.Conditions, clientIp),
                CcRuleType.FrequentError => MatchConditions(context, rule.Conditions, clientIp),
                _ => false
            };
            
            if (!conditionsMatch) continue;
            
            // 获取或创建计数器
            var counterKey = $"{rule.Id}:{clientIp}";
            var counter = rule.Type switch
            {
                CcRuleType.FrequentAccess => GetOrCreateCounter(_counters, counterKey, rule.Period),
                CcRuleType.FrequentAttack => GetOrCreateCounter(_attackCounters, clientIp, rule.Period),
                CcRuleType.FrequentError => GetOrCreateCounter(_errorCounters, clientIp, rule.Period),
                _ => null
            };
            
            if (counter == null) continue;
            
            // 仅对 FrequentAccess 增加计数，其他类型在专门的事件中记录
            if (rule.Type == CcRuleType.FrequentAccess)
            {
                counter.Increment();
            }
            
            // 检查是否达到阈值
            if (counter.Count >= rule.Threshold)
            {
                result.IsTriggered = true;
                result.TriggeredRule = rule;
                result.Action = rule.Action;
                result.ActionSeconds = rule.ActionSeconds;
                result.Message = $"IP {clientIp} 触发 CC 规则 [{rule.Name}]: {rule.Period}秒内请求{counter.Count}次，超过阈值{rule.Threshold}";
                
                // 标记为已触发，设置处罚结束时间
                _triggeredIps[triggeredKey] = DateTime.UtcNow.AddSeconds(rule.ActionSeconds);
                
                // 重置计数器
                counter.Reset();
                
                _logger.Warn("CC 规则触发: {Message}", result.Message);
                return result;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// 记录攻击事件
    /// </summary>
    public void RecordAttackEvent(string clientIp)
    {
        var rules = _statisticService.GetAdvancedCcRulesByType(CcRuleType.FrequentAttack)
            .Where(r => r.Enabled)
            .ToList();
        
        foreach (var rule in rules)
        {
            var counter = GetOrCreateCounter(_attackCounters, clientIp, rule.Period);
            counter.Increment();
        }
    }
    
    /// <summary>
    /// 记录错误事件
    /// </summary>
    public void RecordErrorEvent(string clientIp, int statusCode)
    {
        var rules = _statisticService.GetAdvancedCcRulesByType(CcRuleType.FrequentError)
            .Where(r => r.Enabled)
            .ToList();
        
        foreach (var rule in rules)
        {
            // 检查状态码是否匹配条件
            var statusCondition = rule.Conditions.FirstOrDefault(c => c.Target == CcMatchTarget.StatusCode);
            if (statusCondition != null)
            {
                if (!MatchValue(statusCode.ToString(), statusCondition))
                    continue;
            }
            
            var counter = GetOrCreateCounter(_errorCounters, clientIp, rule.Period);
            counter.Increment();
        }
    }
    
    /// <summary>
    /// 执行 CC 处罚动作
    /// </summary>
    public async Task ExecuteAction(HttpContext context, CcCheckResult result, string clientIp)
    {
        if (!result.IsTriggered || result.TriggeredRule == null) return;
        
        var rule = result.TriggeredRule;
        
        switch (result.Action)
        {
            case CcAction.Block:
                // 封禁 IP
                var blockDuration = TimeSpan.FromSeconds(result.ActionSeconds);
                SharedData.ClientFb.Set(clientIp, $"CC 规则触发: {rule.Name}", blockDuration);
                _logger.Info("CC 封禁: IP={ClientIp}, Rule={Rule}, Duration={Duration}秒",
                    clientIp, rule.Name, result.ActionSeconds);

                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发 CC 防护规则 [{rule.Name}]，已被封禁 {result.ActionSeconds} 秒"
                });
                break;
                
            case CcAction.Captcha:
                // 人机验证 - 目前先用封禁替代，后续可以实现真正的验证码页面
                SharedData.ClientFb.Set(clientIp, $"CC 人机验证: {rule.Name}", TimeSpan.FromSeconds(result.ActionSeconds));
                _logger.Info("CC 人机验证: IP={ClientIp}, Rule={Rule}, Duration={Duration}秒",
                    clientIp, rule.Name, result.ActionSeconds);
                
                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发 CC 防护规则 [{rule.Name}]，需要人机验证"
                });
                break;
                
            case CcAction.Reject:
                // 直接拒绝
                _logger.Info("CC 拒绝: IP={ClientIp}, Rule={Rule}", clientIp, rule.Name);
                
                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发 CC 防护规则 [{rule.Name}]"
                });
                break;
                
            case CcAction.RateLimit:
                // 限速 - 返回 429 Too Many Requests
                _logger.Info("CC 限速: IP={ClientIp}, Rule={Rule}", clientIp, rule.Name);
                
                context.Response.Headers["Retry-After"] = result.ActionSeconds.ToString();
                await WafUtil.WriteErrorOutput(context, 429, new Dictionary<string, string?>
                {
                    ["reason"] = $"请求过于频繁，请 {result.ActionSeconds} 秒后重试"
                });
                break;
                
            case CcAction.LogOnly:
                // 仅记录日志
                _logger.Info("CC 记录: IP={ClientIp}, Rule={Rule}, Message={Message}", 
                    clientIp, rule.Name, result.Message);
                break;
        }
        
        // 记录安全事件
        SharedData.Security.RecordEvent(SecurityEventType.CcAttack, clientIp);
    }
    
    /// <summary>
    /// 匹配所有条件（AND 关系）
    /// </summary>
    private bool MatchConditions(HttpContext context, List<CcCondition> conditions, string clientIp)
    {
        // 没有条件时默认匹配所有请求
        if (conditions.Count == 0) return true;
        
        foreach (var condition in conditions)
        {
            if (!MatchCondition(context, condition, clientIp))
                return false;
        }
        return true;
    }
    
    /// <summary>
    /// 匹配单个条件
    /// </summary>
    private static bool MatchCondition(HttpContext context, CcCondition condition, string clientIp)
    {
        var request = context.Request;
        string? targetValue = condition.Target switch
        {
            CcMatchTarget.UrlPath => request.Path.Value,
            CcMatchTarget.FullUrl => $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}",
            CcMatchTarget.Method => request.Method,
            CcMatchTarget.ContentType => request.ContentType,
            CcMatchTarget.UserAgent => request.Headers.UserAgent.ToString(),
            CcMatchTarget.Referer => request.Headers.Referer.ToString(),
            CcMatchTarget.Header => GetHeaderValue(request, condition.Values.FirstOrDefault()),
            CcMatchTarget.QueryParam => GetQueryParamValue(request, condition.Values.FirstOrDefault()),
            CcMatchTarget.Cookie => GetCookieValue(request, condition.Values.FirstOrDefault()),
            CcMatchTarget.ClientIp => clientIp,
            CcMatchTarget.StatusCode => context.Response.StatusCode.ToString(),
            _ => null
        };
        
        // 对于 Header/QueryParam/Cookie，第一个值是 key，后续值是要匹配的内容
        if (condition.Target is CcMatchTarget.Header or CcMatchTarget.QueryParam or CcMatchTarget.Cookie)
        {
            var matchValues = condition.Values.Skip(1).ToList();
            if (matchValues.Count == 0)
            {
                // 只检查存在性
                return condition.Operator switch
                {
                    CcMatchOperator.Exists => !string.IsNullOrEmpty(targetValue),
                    CcMatchOperator.NotExists => string.IsNullOrEmpty(targetValue),
                    _ => !string.IsNullOrEmpty(targetValue)
                };
            }
            return MatchValue(targetValue, condition.Operator, matchValues);
        }
        
        return MatchValue(targetValue, condition);
    }
    
    /// <summary>
    /// 匹配值
    /// </summary>
    private static bool MatchValue(string? targetValue, CcCondition condition)
    {
        return MatchValue(targetValue, condition.Operator, condition.Values);
    }
    
    private static bool MatchValue(string? targetValue, CcMatchOperator op, List<string> values)
    {
        if (targetValue == null) return op == CcMatchOperator.NotExists;
        
        // 多个值为 OR 关系
        foreach (var value in values)
        {
            bool matched = op switch
            {
                CcMatchOperator.Equal => targetValue.Equals(value, StringComparison.OrdinalIgnoreCase),
                CcMatchOperator.NotEqual => !targetValue.Equals(value, StringComparison.OrdinalIgnoreCase),
                CcMatchOperator.Contains => targetValue.Contains(value, StringComparison.OrdinalIgnoreCase),
                CcMatchOperator.NotContains => !targetValue.Contains(value, StringComparison.OrdinalIgnoreCase),
                CcMatchOperator.StartsWith => targetValue.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                CcMatchOperator.EndsWith => targetValue.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                CcMatchOperator.Regex => MatchRegex(targetValue, value),
                CcMatchOperator.Exists => true,
                CcMatchOperator.NotExists => false,
                _ => false
            };
            
            if (matched) return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// 正则匹配
    /// </summary>
    private static bool MatchRegex(string input, string pattern)
    {
        try
        {
            var regex = _regexCache.GetOrAdd(pattern, p => 
                new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)));
            return regex.IsMatch(input);
        }
        catch
        {
            return false;
        }
    }
    
    private static string? GetHeaderValue(HttpRequest request, string? headerName)
    {
        if (string.IsNullOrEmpty(headerName)) return null;
        return request.Headers.TryGetValue(headerName, out var value) ? value.ToString() : null;
    }
    
    private static string? GetQueryParamValue(HttpRequest request, string? paramName)
    {
        if (string.IsNullOrEmpty(paramName)) return null;
        return request.Query.TryGetValue(paramName, out var value) ? value.ToString() : null;
    }
    
    private static string? GetCookieValue(HttpRequest request, string? cookieName)
    {
        if (string.IsNullOrEmpty(cookieName)) return null;
        return request.Cookies.TryGetValue(cookieName, out var value) ? value : null;
    }
    
    /// <summary>
    /// 获取或创建计数器
    /// </summary>
    private static CcCounter GetOrCreateCounter(ConcurrentDictionary<string, CcCounter> dict, string key, int periodSeconds)
    {
        return dict.AddOrUpdate(
            key,
            _ => new CcCounter(periodSeconds),
            (_, existing) =>
            {
                if (existing.IsExpired())
                {
                    existing.Reset();
                    existing.Period = periodSeconds;
                }
                return existing;
            }
        );
    }
    
    /// <summary>
    /// 清理过期数据
    /// </summary>
    private async Task CleanupLoop()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            
            try
            {
                var now = DateTime.UtcNow;
                
                // 清理过期的触发记录
                var expiredTriggers = _triggeredIps.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList();
                foreach (var key in expiredTriggers)
                {
                    _triggeredIps.TryRemove(key, out _);
                }

                // 清理过期的计数器
                CleanupExpiredCounters(_counters);
                CleanupExpiredCounters(_attackCounters);
                CleanupExpiredCounters(_errorCounters);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CC 清理任务异常");
            }
        }
    }
    
    private static void CleanupExpiredCounters(ConcurrentDictionary<string, CcCounter> dict)
    {
        var expiredKeys = dict.Where(kv => kv.Value.IsExpired()).Select(kv => kv.Key).ToList();
        foreach (var key in expiredKeys)
        {
            dict.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// CC 计数器
/// </summary>
public class CcCounter(int periodSeconds)
{
    private int _count = 0;
    private DateTime _startTime = DateTime.UtcNow;

    public int Period { get; set; } = periodSeconds;
    public int Count => _count;

    public void Increment()
    {
        if (IsExpired())
        {
            Reset();
        }
        Interlocked.Increment(ref _count);
    }
    
    public bool IsExpired()
    {
        return DateTime.UtcNow > _startTime.AddSeconds(Period);
    }
    
    public void Reset()
    {
        _count = 0;
        _startTime = DateTime.UtcNow;
    }
}
