using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using LyWaf.Services.WafRule;
using LyWaf.Utils;
using NLog;

namespace LyWaf.Services.BlackWhiteList;

/// <summary>
/// 黑白名单规则类型
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BwRuleType
{
    /// <summary>白名单（放行）</summary>
    White,
    /// <summary>黑名单（拦截）</summary>
    Black,
}

/// <summary>
/// 黑白名单匹配条件（复用 WafMatchField / WafMatchOperator）
/// </summary>
public class BwCondition
{
    /// <summary>匹配字段</summary>
    public WafMatchField Field { get; set; } = WafMatchField.ClientIp;

    /// <summary>字段名称（Header/Cookie/QueryParam 时需指定）</summary>
    public string? FieldName { get; set; }

    /// <summary>匹配操作符</summary>
    public WafMatchOperator Operator { get; set; } = WafMatchOperator.Equal;

    /// <summary>匹配值</summary>
    public string Value { get; set; } = "";

    /// <summary>忽略大小写</summary>
    public bool IgnoreCase { get; set; } = true;
}

/// <summary>
/// 黑白名单规则
/// </summary>
public class BwRule
{
    /// <summary>规则 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>规则名称</summary>
    public string Name { get; set; } = "";

    /// <summary>规则类型（白名单/黑名单）</summary>
    public BwRuleType Type { get; set; } = BwRuleType.Black;

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>匹配条件列表（AND 逻辑，全部满足才匹配）</summary>
    public List<BwCondition> Conditions { get; set; } = [];

    /// <summary>总命中次数（支持 Interlocked）</summary>
    public long HitCount;

    /// <summary>今日命中次数（支持 Interlocked）</summary>
    public long TodayHitCount;

    /// <summary>今日日期（用于重置 TodayHitCount）</summary>
    [JsonIgnore]
    public string TodayDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>更新时间</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// 黑白名单检查结果
/// </summary>
public class BwCheckResult
{
    public bool IsMatched { get; set; }
    public BwRule? MatchedRule { get; set; }
    public BwRuleType? RuleType { get; set; }
}

/// <summary>
/// 黑白名单命中事件（按 IP+应用 聚合）
/// </summary>
public class BwHitEvent
{
    public string SourceIp { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public string Application { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string RuleType { get; set; } = "";
    public long HitCount;
    public DateTime FirstHitTime { get; set; } = DateTime.UtcNow;
    public DateTime LastHitTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 黑白名单服务接口
/// </summary>
public interface IBlackWhiteListService
{
    /// <summary>初始化（加载规则文件 + 启动定时器）</summary>
    void Initialize();

    /// <summary>获取所有规则</summary>
    List<BwRule> GetRules();

    /// <summary>获取单条规则</summary>
    BwRule? GetRule(string id);

    /// <summary>添加规则</summary>
    bool AddRule(BwRule rule);

    /// <summary>更新规则</summary>
    bool UpdateRule(BwRule rule);

    /// <summary>删除规则</summary>
    bool DeleteRule(string id);

    /// <summary>切换启用状态</summary>
    bool ToggleRule(string id);

    /// <summary>检查请求是否命中规则</summary>
    BwCheckResult CheckRequest(HttpContext context, string clientIp);

    /// <summary>重置所有命中计数</summary>
    void ResetHitCounts();

    /// <summary>记录命中事件（中间件调用）</summary>
    void RecordHit(string sourceIp, string application, string region, string city, string ruleName, string ruleType);

    /// <summary>获取命中事件列表（支持过滤）</summary>
    List<BwHitEvent> GetHitEvents(string? ipFilter = null, string? domainFilter = null,
        DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>清除所有命中事件</summary>
    void ClearHitEvents();
}

public class BlackWhiteListService : IBlackWhiteListService, IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private static readonly string RulesFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.blackwhite.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private List<BwRule> _rules = [];
    private Timer? _saveTimer;
    private bool _hitCountDirty;

    // 正则缓存
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    // 命中事件聚合（Key: "ip|application"）
    private readonly ConcurrentDictionary<string, BwHitEvent> _hitEvents = new();

    public void Initialize()
    {
        LoadRules();
        _logger.Info("黑白名单服务已初始化: {Count} 条规则", _rules.Count);

        // 定时保存命中计数 + 检查日期切换（每 60 秒）
        _saveTimer = new Timer(_ =>
        {
            CheckDateRollover();
            if (_hitCountDirty)
            {
                SaveRules();
                _hitCountDirty = false;
            }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    // ============ CRUD ============

    public List<BwRule> GetRules()
    {
        lock (_lock) return [.. _rules];
    }

    public BwRule? GetRule(string id)
    {
        lock (_lock) return _rules.FirstOrDefault(r => r.Id == id);
    }

    public bool AddRule(BwRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return false;

        lock (_lock)
        {
            if (_rules.Any(r => r.Id == rule.Id)) return false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = null;
            rule.HitCount = 0;
            rule.TodayHitCount = 0;
            rule.TodayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _rules.Add(rule);
            SaveRules();
        }
        return true;
    }

    public bool UpdateRule(BwRule rule)
    {
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index < 0) return false;

            // 保留命中计数
            rule.HitCount = _rules[index].HitCount;
            rule.TodayHitCount = _rules[index].TodayHitCount;
            rule.TodayDate = _rules[index].TodayDate;
            rule.CreatedAt = _rules[index].CreatedAt;
            rule.UpdatedAt = DateTime.UtcNow;
            _rules[index] = rule;
            SaveRules();
        }
        return true;
    }

    public bool DeleteRule(string id)
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return false;
            _rules.Remove(rule);
            SaveRules();
        }
        return true;
    }

    public bool ToggleRule(string id)
    {
        lock (_lock)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return false;
            rule.Enabled = !rule.Enabled;
            rule.UpdatedAt = DateTime.UtcNow;
            SaveRules();
            return true;
        }
    }

    public void ResetHitCounts()
    {
        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                rule.HitCount = 0;
                rule.TodayHitCount = 0;
            }
            SaveRules();
        }
    }

    // ============ 命中事件 ============

    public void RecordHit(string sourceIp, string application, string region, string city, string ruleName, string ruleType)
    {
        var key = $"{sourceIp}|{application}";
        _hitEvents.AddOrUpdate(key,
            // 新增
            _ => new BwHitEvent
            {
                SourceIp = sourceIp,
                Region = region,
                City = city,
                Application = application,
                RuleName = ruleName,
                RuleType = ruleType,
                HitCount = 1,
                FirstHitTime = DateTime.UtcNow,
                LastHitTime = DateTime.UtcNow,
            },
            // 更新
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.HitCount);
                existing.LastHitTime = DateTime.UtcNow;
                existing.RuleName = ruleName;
                return existing;
            });
    }

    public List<BwHitEvent> GetHitEvents(string? ipFilter = null, string? domainFilter = null,
        DateTime? startTime = null, DateTime? endTime = null)
    {
        var query = _hitEvents.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(ipFilter))
            query = query.Where(e => e.SourceIp.Contains(ipFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(domainFilter))
            query = query.Where(e => e.Application.Contains(domainFilter, StringComparison.OrdinalIgnoreCase));
        if (startTime.HasValue)
            query = query.Where(e => e.FirstHitTime >= startTime.Value);
        if (endTime.HasValue)
            query = query.Where(e => e.LastHitTime <= endTime.Value);

        return query.OrderByDescending(e => e.LastHitTime).ToList();
    }

    public void ClearHitEvents()
    {
        _hitEvents.Clear();
    }

    // ============ 请求检查 ============

    public BwCheckResult CheckRequest(HttpContext context, string clientIp)
    {
        var result = new BwCheckResult { IsMatched = false };

        List<BwRule> rules;
        lock (_lock) rules = [.. _rules];

        // 白名单优先检查
        foreach (var rule in rules.Where(r => r.Enabled && r.Type == BwRuleType.White))
        {
            if (MatchAllConditions(context, rule, clientIp))
            {
                Interlocked.Increment(ref rule.HitCount);
                Interlocked.Increment(ref rule.TodayHitCount);
                _hitCountDirty = true;
                return new BwCheckResult
                {
                    IsMatched = true,
                    MatchedRule = rule,
                    RuleType = BwRuleType.White,
                };
            }
        }

        // 黑名单检查
        foreach (var rule in rules.Where(r => r.Enabled && r.Type == BwRuleType.Black))
        {
            if (MatchAllConditions(context, rule, clientIp))
            {
                Interlocked.Increment(ref rule.HitCount);
                Interlocked.Increment(ref rule.TodayHitCount);
                _hitCountDirty = true;
                return new BwCheckResult
                {
                    IsMatched = true,
                    MatchedRule = rule,
                    RuleType = BwRuleType.Black,
                };
            }
        }

        return result;
    }

    // ============ 条件匹配 ============

    private bool MatchAllConditions(HttpContext context, BwRule rule, string clientIp)
    {
        if (rule.Conditions.Count == 0) return false;

        // AND 逻辑：所有条件都要满足
        foreach (var condition in rule.Conditions)
        {
            if (!MatchCondition(context, condition, clientIp))
                return false;
        }
        return true;
    }

    private static bool MatchCondition(HttpContext context, BwCondition condition, string clientIp)
    {
        var request = context.Request;
        string? targetValue = condition.Field switch
        {
            WafMatchField.UriPath => request.Path.Value,
            WafMatchField.FullUrl => $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}",
            WafMatchField.QueryString => request.QueryString.Value,
            WafMatchField.Method => request.Method,
            WafMatchField.ClientIp => clientIp,
            WafMatchField.XForwardedFor => request.Headers["X-Forwarded-For"].ToString(),
            WafMatchField.UserAgent => request.Headers.UserAgent.ToString(),
            WafMatchField.Referer => request.Headers.Referer.ToString(),
            WafMatchField.ContentType => request.ContentType,
            WafMatchField.ContentLength => request.ContentLength?.ToString(),
            WafMatchField.Cookie => GetFieldValue(request.Cookies, condition.FieldName),
            WafMatchField.Header => request.Headers.TryGetValue(condition.FieldName ?? "", out var hv) ? hv.ToString() : null,
            WafMatchField.QueryParam => request.Query.TryGetValue(condition.FieldName ?? "", out var qv) ? qv.ToString() : null,
            WafMatchField.Body => null,
            WafMatchField.ServerPort => context.Connection.LocalPort.ToString(),
            _ => null,
        };

        // 存在性检查
        if (condition.Operator == WafMatchOperator.Exists)
            return !string.IsNullOrEmpty(targetValue);
        if (condition.Operator == WafMatchOperator.NotExists)
            return string.IsNullOrEmpty(targetValue);

        if (targetValue == null) return false;

        // 长度比较
        if (condition.Operator == WafMatchOperator.LengthGreaterThan)
            return int.TryParse(condition.Value, out var gt) && targetValue.Length > gt;
        if (condition.Operator == WafMatchOperator.LengthLessThan)
            return int.TryParse(condition.Value, out var lt) && targetValue.Length < lt;

        var comparison = condition.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return condition.Operator switch
        {
            WafMatchOperator.Equal => targetValue.Equals(condition.Value, comparison),
            WafMatchOperator.NotEqual => !targetValue.Equals(condition.Value, comparison),
            WafMatchOperator.Contains => targetValue.Contains(condition.Value, comparison),
            WafMatchOperator.NotContains => !targetValue.Contains(condition.Value, comparison),
            WafMatchOperator.StartsWith => targetValue.StartsWith(condition.Value, comparison),
            WafMatchOperator.EndsWith => targetValue.EndsWith(condition.Value, comparison),
            WafMatchOperator.Regex => MatchRegex(targetValue, condition.Value, condition.IgnoreCase),
            _ => false,
        };
    }

    private static string? GetFieldValue(IRequestCookieCollection cookies, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return cookies.TryGetValue(name, out var value) ? value : null;
    }

    private static bool MatchRegex(string input, string pattern, bool ignoreCase)
    {
        try
        {
            var key = ignoreCase ? $"i:{pattern}" : pattern;
            var options = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var regex = _regexCache.GetOrAdd(key, _ =>
                new Regex(pattern, options, TimeSpan.FromSeconds(1)));
            return regex.IsMatch(input);
        }
        catch
        {
            return false;
        }
    }

    // ============ 持久化 ============

    private void LoadRules()
    {
        try
        {
            if (File.Exists(RulesFilePath))
            {
                var json = File.ReadAllText(RulesFilePath);
                _rules = JsonSerializer.Deserialize<List<BwRule>>(json, _jsonOptions) ?? [];
                // 初始化 TodayDate
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                foreach (var rule in _rules)
                {
                    if (rule.TodayDate != today)
                    {
                        rule.TodayHitCount = 0;
                        rule.TodayDate = today;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载黑白名单规则失败");
            _rules = [];
        }
    }

    private void SaveRules()
    {
        try
        {
            var json = JsonSerializer.Serialize(_rules, _jsonOptions);
            File.WriteAllText(RulesFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存黑白名单规则失败");
        }
    }

    private void CheckDateRollover()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            foreach (var rule in _rules)
            {
                if (rule.TodayDate != today)
                {
                    rule.TodayHitCount = 0;
                    rule.TodayDate = today;
                    _hitCountDirty = true;
                }
            }
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;

        // 关闭前保存一次命中计数
        if (_hitCountDirty)
        {
            try { SaveRules(); } catch { /* best-effort */ }
            _hitCountDirty = false;
        }
    }
}
