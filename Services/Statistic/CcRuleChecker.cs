using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LyWaf.Services.Captcha;
using LyWaf.Shared;
using LyWaf.Utils;
using LyWaf.Services.RuntimeState;
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
    public bool IsNewTrigger { get; set; }
    public string? StateKey { get; set; }
    public CcDecision? Decision { get; set; }
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
    void RecordAttackEvent(HttpContext context, string clientIp);
    
    /// <summary>
    /// 记录错误事件（用于 FrequentError 类型）
    /// </summary>
    void RecordErrorEvent(HttpContext context, string clientIp, int statusCode);
    
    /// <summary>
    /// 执行 CC 处罚动作
    /// </summary>
    Task ExecuteAction(HttpContext context, CcCheckResult result, string clientIp);

    /// <summary>
    /// 清除指定 IP 的 CC 触发记录和计数器（验证码通过后调用）
    /// </summary>
    void ClearTriggeredRecords(string clientIp);
    string? GetCaptchaSubject(HttpContext context, string clientIp) => null;
    void RecordCaptchaInterception(HttpContext context, string clientIp) { }
    /// <summary>Release one rule/scope/subject locally and publish its fence. Callers must authorize this operation; no peer ACK is implied.</summary>
    CcPolicyGeneration ReleaseRule(HttpContext context, AdvancedCcRule rule, string clientIp);
    CcPolicyGeneration ReleaseRule(string scope, AdvancedCcRule rule, string clientIp);
}

public class CcRuleChecker : ICcRuleChecker
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IStatisticService _statisticService;
    private readonly ICaptchaService _captchaService;
    private readonly IRuntimeStateStore _runtimeState;
    private readonly CcDecisionService _decisions;
    private readonly CcIngressOwnership? _ingress;

    // CC 计数器存储: Key = "{ruleId}:{clientIp}", Value = 请求计数信息
    private static readonly ConcurrentDictionary<string, CcCounter> _counters = new();

    // 攻击事件计数: Key = "{clientIp}", Value = 攻击计数
    private static readonly ConcurrentDictionary<string, CcCounter> _attackCounters = new();

    // 错误事件计数: Key = "{clientIp}", Value = 错误计数
    private static readonly ConcurrentDictionary<string, CcCounter> _errorCounters = new();

    // 编译后的正则表达式缓存
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    public CcRuleChecker(IStatisticService statisticService, ICaptchaService captchaService, IRuntimeStateStore runtimeState,
        CcDecisionService? decisions = null, CcIngressOwnership? ingress = null)
    {
        _statisticService = statisticService;
        _captchaService = captchaService;
        _runtimeState = runtimeState;
        _decisions = decisions ?? new CcDecisionService(statisticService, runtimeState, CcBanLog.Shared);
        _ingress = ingress;

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
            if (!CcRequestIdentity.TrySubject(context, rule, clientIp, out var subject)) continue;
            // 检查是否已经在处罚期内
            var triggeredKey = CcRuleStateKey.Current(_runtimeState, rule, clientIp, context);
            var policy = _decisions.Register(rule, CcRuleStateKey.Scope(context), subject);
            var currentDecision = _decisions.Evaluate(policy, rule, GetWindowStart(rule.Period), 0).Decision;
            if (_runtimeState.TryGet<DateTimeOffset>("cc.triggered", triggeredKey, out var distributedUntil)
                && distributedUntil > DateTimeOffset.UtcNow
                && (!rule.RequireCompleteCoverage || currentDecision?.ExpiresAt == distributedUntil))
            {
                result.IsTriggered = true;
                result.TriggeredRule = rule;
                result.Action = rule.Action;
                result.ActionSeconds = rule.ActionSeconds;
                result.Message = $"IP {clientIp} 仍在 CC 规则 [{rule.Name}] 处罚期内";
                result.StateKey = triggeredKey;
                result.Decision = currentDecision;
                return result;
            }
            
            // 根据规则类型检查
            bool conditionsMatch = rule.Type switch
            {
                CcRuleType.FrequentAccess => MatchRuleConditions(context, rule, clientIp),
                CcRuleType.FrequentAttack => MatchRuleConditions(context, rule, clientIp),
                CcRuleType.FrequentError => MatchRuleConditions(context, rule, clientIp),
                _ => false
            };
            
            if (!conditionsMatch) continue;
            
            var counterKey = triggeredKey;
            var windowStart = GetWindowStart(rule.Period);
            var count = rule.Type switch
            {
                CcRuleType.FrequentAccess => _ingress?.ShouldCount(context, rule, policy) == false
                    ? _runtimeState.GetWindowValue("cc.access", counterKey, windowStart)
                    : _runtimeState.IncrementWindow("cc.access", counterKey, windowStart, TimeSpan.FromSeconds(rule.Period)),
                CcRuleType.FrequentAttack => _runtimeState.GetWindowValue("cc.attack", counterKey, windowStart),
                CcRuleType.FrequentError => _runtimeState.GetWindowValue("cc.error", counterKey, windowStart),
                _ => 0
            };
            
            // 检查是否达到阈值
            var evaluated = _decisions.Evaluate(policy, rule, windowStart, count);
            if (evaluated.Decision is { } decision)
            {
                result.IsTriggered = true;
                result.TriggeredRule = rule;
                result.Action = rule.Action;
                result.ActionSeconds = rule.ActionSeconds;
                result.Message = $"IP {clientIp} 触发 CC 规则 [{rule.Name}]: {rule.Period}秒内请求{count}次，超过阈值{rule.Threshold}";
                result.IsNewTrigger = evaluated.Created;
                result.StateKey = triggeredKey;
                result.Decision = decision;
                
                if (evaluated.Created) _logger.Warn("CC 规则触发: {Message}", result.Message);
                return result;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// 记录攻击事件
    /// </summary>
    public void RecordAttackEvent(HttpContext context, string clientIp)
    {
        var rules = _statisticService.GetAdvancedCcRulesByType(CcRuleType.FrequentAttack)
            .Where(r => r.Enabled)
            .ToList();
        
        foreach (var rule in rules)
        {
            if (!CcRequestIdentity.TrySubject(context, rule, clientIp, out var subject)) continue;
            if (!MatchRuleConditions(context, rule, clientIp)) continue;
            var key = CcRuleStateKey.Current(_runtimeState, rule, clientIp, context);
            _decisions.Register(rule, CcRuleStateKey.Scope(context), subject);
            _runtimeState.IncrementWindow("cc.attack", key, GetWindowStart(rule.Period), TimeSpan.FromSeconds(rule.Period));
        }
    }
    
    /// <summary>
    /// 记录错误事件
    /// </summary>
    public void RecordErrorEvent(HttpContext context, string clientIp, int statusCode)
    {
        var rules = _statisticService.GetAdvancedCcRulesByType(CcRuleType.FrequentError)
            .Where(r => r.Enabled)
            .ToList();
        
        foreach (var rule in rules)
        {
            if (!CcRequestIdentity.TrySubject(context, rule, clientIp, out var subject)) continue;
            if (!MatchRuleConditions(context, rule, clientIp)) continue;
            var key = CcRuleStateKey.Current(_runtimeState, rule, clientIp, context);
            _decisions.Register(rule, CcRuleStateKey.Scope(context), subject);
            _runtimeState.IncrementWindow("cc.error", key, GetWindowStart(rule.Period), TimeSpan.FromSeconds(rule.Period));
        }
    }
    
    /// <summary>
    /// 执行 CC 处罚动作
    /// </summary>
    public async Task ExecuteAction(HttpContext context, CcCheckResult result, string clientIp)
    {
        if (!result.IsTriggered || result.TriggeredRule == null) return;
        
        var rule = result.TriggeredRule;
        if (result.StateKey is not null && result.StateKey != CcRuleStateKey.Current(_runtimeState, rule, clientIp, context))
        {
            result.IsTriggered = false;
            return;
        }
        
        switch (result.Action)
        {
            case CcAction.Block:
                // cc.triggered is the scoped authority; never promote a CC decision to a global IP ban.
                var blockDuration = TimeSpan.FromSeconds(result.ActionSeconds);
                var newBan = result.Decision is null && result.IsNewTrigger && _runtimeState.TryClaim("cc.ban.logged",
                    result.StateKey ?? CcRuleStateKey.Current(_runtimeState, rule, clientIp, context), blockDuration);
                if (newBan) CcBanLog.Shared.Add(clientIp, $"CC 规则触发: {rule.Name}", blockDuration);
                if (newBan) _logger.Info("CC 封禁: IP={ClientIp}, Rule={Rule}, Duration={Duration}秒",
                    clientIp, rule.Name, result.ActionSeconds);

                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = newBan ? $"触发 CC 防护规则 [{rule.Name}]，已被封禁 {result.ActionSeconds} 秒" : "IP 仍处于封禁期内"
                });
                break;
                
            case CcAction.Captcha:
                // CaptchaMiddleware reads the same scoped trigger without a second pending cache.
                _logger.Info("CC 人机验证: IP={ClientIp}, Rule={Rule}, Duration={Duration}秒",
                    clientIp, rule.Name, result.ActionSeconds);

                // 返回 403 + captcha 标识，让中间件或客户端知道需要验证
                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发 CC 防护规则 [{rule.Name}]，需要人机验证",
                    ["captcha"] = "required"
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
        if (result.Decision is not null && result.Action != CcAction.LogOnly)
            _decisions.RecordHit(result.Decision, rule.Name);
        SharedData.Security.RecordEvent(SecurityEventType.CcAttack, clientIp);
    }

    /// <summary>
    /// 清除指定 IP 的 CC 触发记录和计数器（验证码通过后调用）
    /// </summary>
    public void ClearTriggeredRecords(string clientIp)
    {
        if (CcPolicyGeneration.TryParseKey(clientIp, out var policyKey, out var expected))
        {
            // A delayed captcha completion must not release a newer penalty generation.
            _runtimeState.AdvanceCcPolicyGeneration(policyKey, expected);
            return;
        }

        // Legacy IP-wide captcha completion releases only known CC scopes, never manual IP bans.
        var policies = _runtimeState.GetSnapshots()
            .Where(s => CcPolicyGeneration.IsStateDomain(s.Domain) || s.Domain == CcPolicyGeneration.Domain)
            .Select(s => CcPolicyGeneration.TryParseKey(s.Key, out var key, out _) ? key : null)
            .Where(key => key is not null && key.EndsWith($":{clientIp}", StringComparison.Ordinal))
            .Distinct().ToArray();
        foreach (var key in policies) _runtimeState.AdvanceCcPolicyGeneration(key!);

        // 清除计数器
        var counterKeys = _counters.Keys.Where(k => k.EndsWith($":{clientIp}")).ToList();
        foreach (var key in counterKeys)
        {
            _counters.TryRemove(key, out _);
        }

        _attackCounters.TryRemove(clientIp, out _);
        _errorCounters.TryRemove(clientIp, out _);

        _logger.Info("已清除 IP={ClientIp} 的 CC 统计记录", clientIp);
    }

    public string? GetCaptchaSubject(HttpContext context, string clientIp)
    {
        foreach (var rule in _statisticService.GetAdvancedCcRules().Where(r => r.Enabled && r.Action == CcAction.Captcha).OrderBy(r => r.Priority))
        {
            var key = CcRuleStateKey.Current(_runtimeState, rule, clientIp, context);
            if (_runtimeState.TryGet<DateTimeOffset>("cc.triggered", key, out var until) && until > DateTimeOffset.UtcNow)
                return key;
        }
        return null;
    }

    public void RecordCaptchaInterception(HttpContext context, string clientIp)
    {
        foreach (var rule in _statisticService.GetAdvancedCcRules().Where(r => r.Enabled && r.Action == CcAction.Captcha).OrderBy(r => r.Priority))
        {
            var policy = _decisions.Register(rule, CcRuleStateKey.Scope(context), clientIp);
            var decision = _decisions.Evaluate(policy, rule, GetWindowStart(rule.Period), 0).Decision;
            if (decision is null) continue;
            _decisions.RecordHit(decision, rule.Name);
            return;
        }
    }

    public CcPolicyGeneration ReleaseRule(HttpContext context, AdvancedCcRule rule, string clientIp)
    {
        if (!CcRequestIdentity.TrySubject(context, rule, clientIp, out var subject)) throw new InvalidOperationException("Trusted CC subject unavailable");
        return ReleaseRule(CcRuleStateKey.ResolveScope(rule, CcRuleStateKey.Scope(context), _runtimeState.GetStatus().NodeId), rule, subject);
    }

    public CcPolicyGeneration ReleaseRule(string scope, AdvancedCcRule rule, string clientIp)
        => _runtimeState.AdvanceCcPolicyGeneration(CcRuleStateKey.Create(rule, clientIp, scope));

    /// <summary>
    /// 匹配所有条件（AND 关系）
    /// </summary>
    internal static bool MatchRuleConditions(HttpContext context, AdvancedCcRule rule, string clientIp)
    {
        if (rule.ConditionGroups is not { } groups) return MatchConditions(context, rule.Conditions, clientIp);
        if (groups.Count == 0) return false;
        bool Matches(CcConditionGroup group) => group.Conditions.Count > 0 && (group.Operator switch
        {
            "And" => group.Conditions.All(c => MatchCondition(context, c, clientIp)),
            "Or" => group.Conditions.Any(c => MatchCondition(context, c, clientIp)),
            _ => false
        });
        return rule.GroupOperator switch { "And" => groups.All(Matches), "Or" => groups.Any(Matches), _ => false };
    }

    private static bool MatchConditions(HttpContext context, List<CcCondition> conditions, string clientIp)
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

    private static DateTimeOffset GetWindowStart(int periodSeconds)
    {
        var period = Math.Max(1, periodSeconds);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(now - now % period);
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
