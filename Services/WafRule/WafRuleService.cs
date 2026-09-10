using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LyWaf.Services.Captcha;
using LyWaf.Services.Protect;
using LyWaf.Services.RuntimeState;
using LyWaf.Shared;
using LyWaf.Utils;
using Microsoft.Extensions.Primitives;
using NLog;

namespace LyWaf.Services.WafRule;

/// <summary>
/// WAF 自定义规则服务接口
/// </summary>
public interface IWafRuleService
{
    /// <summary>初始化（加载规则文件）</summary>
    void Initialize();

    /// <summary>获取所有规则（合并三个来源）</summary>
    List<WafCustomRule> GetRules();

    /// <summary>获取单条规则</summary>
    WafCustomRule? GetRule(string id);

    /// <summary>添加规则（仅用户规则）</summary>
    bool AddRule(WafCustomRule rule);

    /// <summary>更新规则（仅用户规则）</summary>
    bool UpdateRule(WafCustomRule rule);

    /// <summary>删除规则（仅用户规则）</summary>
    bool DeleteRule(string id);

    /// <summary>切换启用状态（所有来源）</summary>
    bool ToggleRule(string id);

    /// <summary>检查请求是否命中规则</summary>
    Task<WafRuleCheckResult> CheckRequest(HttpContext context, string clientIp);

    /// <summary>执行规则动作</summary>
    Task ExecuteAction(HttpContext context, WafRuleCheckResult result, string clientIp);
}

public class WafRuleService : IWafRuleService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private static readonly string RulesFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.rules.json");
    private static readonly string ToggleFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.rule-toggles.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ICaptchaService _captchaService;
    private readonly IConfiguration _configuration;
    private readonly IRuntimeStateStore _runtimeState;
    private readonly IRequestInspectionService _inspection;
    private readonly IRulePackageService? _rulePackages;
    private readonly object _lock = new();

    // 三个来源的规则列表
    private List<WafCustomRule> _userRules = [];
    private List<WafCustomRule> _configRules = [];
    private List<WafCustomRule> _systemRules = [];

    // 合并后的规则列表（按优先级排序：用户 > 配置 > 系统）
    private List<WafCustomRule> _mergedRules = [];

    // 配置/系统规则的开关覆盖（持久化到 .lywaf.rule-toggles.json）
    private Dictionary<string, bool> _toggleOverrides = new();

    // 正则缓存
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    public WafRuleService(ICaptchaService captchaService, IConfiguration configuration, IRuntimeStateStore runtimeState,
        IRequestInspectionService inspection)
        : this(captchaService, configuration, runtimeState, inspection, null) { }

    public WafRuleService(ICaptchaService captchaService, IConfiguration configuration, IRuntimeStateStore runtimeState,
        IRequestInspectionService inspection, IRulePackageService? rulePackages)
    {
        _captchaService = captchaService;
        _configuration = configuration;
        _runtimeState = runtimeState;
        _inspection = inspection;
        _rulePackages = rulePackages;

        // 监听 config.ly 变化，自动重新加载配置规则
        ChangeToken.OnChange(
            () => _configuration.GetReloadToken(),
            () =>
            {
                lock (_lock)
                {
                    LoadConfigRules();
                    RebuildMergedRules();
                    _logger.Info("config.ly 变更，已重新加载配置规则: {Count} 条", _configRules.Count);
                }
            });
    }

    public void Initialize()
    {
        LoadToggleOverrides();
        LoadUserRules();
        LoadSystemRules();
        LoadConfigRules();
        RebuildMergedRules();
        _logger.Info("WAF 规则服务已初始化: 用户 {User} 条, 配置 {Config} 条, 系统 {System} 条, 合计 {Total} 条",
            _userRules.Count, _configRules.Count, _systemRules.Count, _mergedRules.Count);
    }

    // ============ CRUD ============

    public List<WafCustomRule> GetRules()
    {
        lock (_lock) return [.. _mergedRules];
    }

    public WafCustomRule? GetRule(string id)
    {
        lock (_lock) return _mergedRules.FirstOrDefault(r => r.Id == id);
    }

    public bool AddRule(WafCustomRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || !HasValidConditions(rule)) return false;
        rule.Source = WafRuleSource.User; // 强制设为用户规则

        lock (_lock)
        {
            if (_userRules.Any(r => r.Id == rule.Id)) return false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = null;
            _userRules.Add(rule);
            SaveUserRules();
            RebuildMergedRules();
        }
        return true;
    }

    public bool UpdateRule(WafCustomRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || !HasValidConditions(rule)) return false;
        lock (_lock)
        {
            var index = _userRules.FindIndex(r => r.Id == rule.Id);
            if (index < 0) return false; // 只能修改用户规则
            rule.Source = WafRuleSource.User;
            rule.CreatedAt = _userRules[index].CreatedAt;
            rule.UpdatedAt = DateTime.UtcNow;
            _userRules[index] = rule;
            SaveUserRules();
            RebuildMergedRules();
        }
        return true;
    }

    public bool DeleteRule(string id)
    {
        lock (_lock)
        {
            var rule = _userRules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return false; // 只能删除用户规则
            _userRules.Remove(rule);
            SaveUserRules();
            RebuildMergedRules();
        }
        return true;
    }

    public bool ToggleRule(string id)
    {
        lock (_lock)
        {
            var rule = _mergedRules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return false;

            rule.Enabled = !rule.Enabled;

            if (rule.Source == WafRuleSource.User)
            {
                // 用户规则：直接修改并保存到 .lywaf.rules.json
                rule.UpdatedAt = DateTime.UtcNow;
                SaveUserRules();
            }
            else
            {
                // 配置/系统规则：保存开关状态到独立文件
                _toggleOverrides[rule.Id] = rule.Enabled;
                SaveToggleOverrides();
            }

            RebuildMergedRules();
        }
        return true;
    }

    // ============ 请求检查 ============

    public async Task<WafRuleCheckResult> CheckRequest(HttpContext context, string clientIp)
    {
        var result = new WafRuleCheckResult { IsTriggered = false };

        List<WafCustomRule> rules;
        lock (_lock) rules = [.. _mergedRules];
        if (_rulePackages != null) rules.AddRange(_rulePackages.GetRules(context));

        var enabledRules = rules
            .Where(r => r.Enabled && IsRuleBoundToSite(context, r))
            .ToList(); // _mergedRules 已按优先级排好序
        RequestInspectionSnapshot? snapshot = null;
        if (enabledRules.Any(rule => rule.ConditionGroups.Any(group =>
                group.Conditions.Any(condition => condition.Field is WafMatchField.QueryString or WafMatchField.Body))))
            snapshot = await _inspection.InspectAsync(context, context.RequestAborted);

        foreach (var rule in enabledRules)
        {
            if (!HasConditions(rule)) continue;

            if (MatchRule(context, rule, clientIp, snapshot))
            {
                result.IsTriggered = true;
                result.TriggeredRule = rule;
                result.Action = rule.Action;
                result.Message = $"IP {clientIp} 命中WAF规则 [{rule.Name}] (来源: {rule.Source})";
                context.Items["LyWaf.RuleId"] = rule.Id;
                context.Items["LyWaf.AttackType"] = "CustomRule";
                _logger.Info("WAF 规则命中: {Message}", result.Message);
                return result;
            }
        }

        return result;
    }

    private static bool IsRuleBoundToSite(HttpContext context, WafCustomRule rule)
    {
        if (rule.Source != WafRuleSource.User) return true;
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<Yarp.ReverseProxy.Model.RouteModel>()?.Config.Metadata;
        return metadata?.TryGetValue(LyWaf.Services.Sites.ManagedSiteProvider.RuleIdsKey, out var ids) != true ||
            (ids ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Contains(rule.Id, StringComparer.Ordinal);
    }

    private static bool HasConditions(WafCustomRule rule) =>
        rule.ConditionGroups?.Any(group => group.Conditions?.Count > 0) == true;

    internal static bool HasValidConditions(WafCustomRule rule)
    {
        return rule.ConditionGroups is { Count: > 0 and <= 5 } &&
               rule.ConditionGroups.All(group => group.Conditions is { Count: > 0 }) &&
               rule.ConditionGroups.Sum(group => group.Conditions?.Count ?? 0) <= 20;
    }

    internal static bool MatchRule(HttpContext context, WafCustomRule rule, string clientIp,
        RequestInspectionSnapshot? snapshot = null)
    {
        var fields = new List<string>();
        var locations = new List<WafMatchLocation>();
        context.Items.Remove("LyWaf.MatchedFields");
        context.Items.Remove("LyWaf.MatchLocations");
        var matches = rule.ConditionGroups.Select((group, index) =>
        {
            var groupFields = new List<string>();
            var groupLocations = new List<WafMatchLocation>();
            var matched = MatchConditions(context, group.Conditions, group.Operator, clientIp, snapshot, groupFields, groupLocations, index + 1);
            if (matched) { fields.AddRange(groupFields); locations.AddRange(groupLocations); }
            return matched;
        });
        var triggered = rule.GroupOperator == WafLogicalOperator.And ? matches.All(x => x) : matches.Any(x => x);
        if (triggered)
        {
            context.Items["LyWaf.MatchedFields"] = fields.Distinct().ToArray();
            context.Items["LyWaf.MatchLocations"] = locations.ToArray();
        }
        return triggered;
    }

    private static bool MatchConditions(HttpContext context, IReadOnlyList<WafCondition> conditions,
        WafLogicalOperator logicalOperator, string clientIp, RequestInspectionSnapshot? snapshot, List<string> fields,
        List<WafMatchLocation> locations, int group)
    {
        if (conditions.Count == 0) return false;
        var index = 0;
        bool Match(WafCondition condition)
        {
            index++;
            var result = MatchCondition(context, condition, clientIp, snapshot);
            if (result)
            {
                fields.Add(condition.Field.ToString());
                locations.Add(new(group, index, condition.Field.ToString(), condition.FieldName?[..Math.Min(condition.FieldName.Length, 128)], condition.Operator.ToString()));
            }
            return result;
        }
        return logicalOperator == WafLogicalOperator.And
            ? conditions.All(Match)
            : conditions.Any(Match);
    }

    // ============ 动作执行 ============

    public async Task ExecuteAction(HttpContext context, WafRuleCheckResult result, string clientIp)
    {
        if (!result.IsTriggered || result.TriggeredRule == null) return;

        var rule = result.TriggeredRule;

        switch (result.Action)
        {
            case WafRuleAction.Block:
                var duration = TimeSpan.FromSeconds(rule.ActionSeconds);
                SharedData.ClientFb.Set(clientIp, $"WAF规则触发: {rule.Name}", duration);
                _runtimeState.Set("ip.blocked", clientIp, $"WAF规则触发: {rule.Name}", duration);
                _logger.Info("WAF 封禁: IP={ClientIp}, Rule={Rule}, Duration={Duration}秒",
                    clientIp, rule.Name, rule.ActionSeconds);
                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发防护规则 [{rule.Name}]，已被封禁 {rule.ActionSeconds} 秒"
                });
                break;

            case WafRuleAction.Captcha:
                _captchaService.SetPending(clientIp, rule.Name, rule.ActionSeconds);
                _logger.Info("WAF 人机验证: IP={ClientIp}, Rule={Rule}", clientIp, rule.Name);
                await WafUtil.WriteErrorOutput(context, 403, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发防护规则 [{rule.Name}]，需要人机验证",
                    ["captcha"] = "required"
                });
                break;

            case WafRuleAction.Reject:
                _logger.Info("WAF 拒绝: IP={ClientIp}, Rule={Rule}, StatusCode={Code}",
                    clientIp, rule.Name, rule.ResponseCode);
                await WafUtil.WriteErrorOutput(context, rule.ResponseCode, new Dictionary<string, string?>
                {
                    ["reason"] = $"触发防护规则 [{rule.Name}]"
                });
                break;

            case WafRuleAction.Observe:
                // 仅记录，不执行拦截
                _logger.Info("WAF 观察: IP={ClientIp}, Rule={Rule}, Message={Message}",
                    clientIp, rule.Name, result.Message);
                break;
        }

        // 记录安全事件
        SharedData.Security.RecordEvent(SecurityEventType.WafIntercept, clientIp);
    }

    // ============ 条件匹配 ============

    private static bool MatchCondition(HttpContext context, WafCondition condition, string clientIp,
        RequestInspectionSnapshot? snapshot)
    {
        var request = context.Request;
        if (condition.Field == WafMatchField.Body)
            return MatchCandidateValues(snapshot?.BodyValues ?? [], condition);
        if (condition.Field == WafMatchField.QueryString && snapshot is not null)
            return MatchCandidateValues([request.QueryString.Value ?? "", .. snapshot.QueryValues], condition);

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
            WafMatchField.Cookie => GetCookieValue(request, condition.FieldName),
            WafMatchField.Header => GetHeaderValue(request, condition.FieldName),
            WafMatchField.QueryParam => GetQueryParamValue(request, condition.FieldName),
            WafMatchField.ServerPort => context.Connection.LocalPort.ToString(),
            _ => null
        };

        return MatchTargetValue(targetValue, condition);
    }

    private static bool MatchCandidateValues(IReadOnlyList<string> values, WafCondition condition)
    {
        if (condition.Operator is WafMatchOperator.NotExists or WafMatchOperator.NotEqual or WafMatchOperator.NotContains)
            return values.All(value => MatchTargetValue(value, condition));
        return values.Any(value => MatchTargetValue(value, condition));
    }

    private static bool MatchTargetValue(string? targetValue, WafCondition condition)
    {
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
            _ => false
        };
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

    private static string? GetHeaderValue(HttpRequest request, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    private static string? GetQueryParamValue(HttpRequest request, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return request.Query.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    private static string? GetCookieValue(HttpRequest request, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return request.Cookies.TryGetValue(name, out var value) ? value : null;
    }

    // ============ 多源加载 ============

    /// <summary>加载用户规则（从 .lywaf.rules.json）</summary>
    private void LoadUserRules()
    {
        try
        {
            if (File.Exists(RulesFilePath))
            {
                var json = File.ReadAllText(RulesFilePath);
                var loadedRules = JsonSerializer.Deserialize<List<WafCustomRule>>(json, _jsonOptions) ?? [];
                _userRules = loadedRules.Where(HasValidConditions).ToList();
                var ignoredCount = loadedRules.Count - _userRules.Count;
                if (ignoredCount > 0)
                    _logger.Warn("忽略 {Count} 条旧格式或条件组无效的用户 WAF 规则", ignoredCount);

                foreach (var rule in _userRules)
                {
                    rule.Source = WafRuleSource.User;
                }
            }
            else
            {
                _userRules = [];
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载用户 WAF 规则失败");
            _userRules = [];
        }
    }

    /// <summary>加载系统内置规则</summary>
    private void LoadSystemRules()
    {
        _systemRules = SystemWafRules.GetRules();
        foreach (var rule in _systemRules)
        {
            rule.Source = WafRuleSource.System;
            // 应用开关覆盖
            if (_toggleOverrides.TryGetValue(rule.Id, out var enabled))
                rule.Enabled = enabled;
        }
    }

    /// <summary>
    /// 加载配置规则（从 config.ly 的 Protect.WafRules 块，直接遍历子节点）
    /// <para>
    /// config.ly 格式（每条规则是一个命名块，仅支持条件组格式）：
    /// <code>
    /// Protect {
    ///     WafRules {
    ///         block-admin-post {
    ///             Name = "拦截 admin POST"
    ///             GroupOperator = Or
    ///             ConditionGroups = [
    ///                 { Operator = And; Conditions = [
    ///                     { Field = UriPath; Operator = StartsWith; Value = "/admin" },
    ///                     { Field = Method; Operator = Equal; Value = "POST" },
    ///                 ]},
    ///             ]
    ///         }
    ///     }
    /// }
    /// </code>
    /// </para>
    /// </summary>
    private void LoadConfigRules()
    {
        var section = _configuration.GetSection("Protect:WafRules");
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            _configRules = [];
            return;
        }

        var rules = new List<WafCustomRule>();
        foreach (var child in children)
        {
            try
            {
                var item = new WafRuleConfigItem();
                child.Bind(item);

                if (item.ConditionGroups.Count == 0)
                    continue;

                var rule = item.ToRule(child.Key);
                if (!HasValidConditions(rule))
                {
                    _logger.Warn("配置规则 [{Key}] 条件组无效，已跳过", child.Key);
                    continue;
                }
                // 应用开关覆盖
                if (_toggleOverrides.TryGetValue(rule.Id, out var enabled))
                    rule.Enabled = enabled;

                rules.Add(rule);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "解析配置规则 [{Key}] 失败，已跳过", child.Key);
            }
        }

        _configRules = rules;
    }

    /// <summary>
    /// 重建合并规则列表（按来源分层排序：用户 > 配置 > 系统，同层内按 priority）
    /// </summary>
    private void RebuildMergedRules()
    {
        var all = new List<WafCustomRule>();
        all.AddRange(_userRules);
        all.AddRange(_configRules);
        all.AddRange(_systemRules);

        _mergedRules = all
            .OrderBy(r => r.Source switch
            {
                WafRuleSource.User => 0,
                WafRuleSource.Config => 1,
                WafRuleSource.System => 2,
                _ => 3,
            })
            .ThenBy(r => r.Priority)
            .ToList();
    }

    // ============ 持久化 ============

    /// <summary>保存用户规则到 .lywaf.rules.json</summary>
    private void SaveUserRules()
    {
        try
        {
            var json = JsonSerializer.Serialize(_userRules, _jsonOptions);
            File.WriteAllText(RulesFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存用户 WAF 规则失败");
        }
    }

    /// <summary>加载配置/系统规则的开关覆盖状态</summary>
    private void LoadToggleOverrides()
    {
        try
        {
            if (File.Exists(ToggleFilePath))
            {
                var json = File.ReadAllText(ToggleFilePath);
                _toggleOverrides = JsonSerializer.Deserialize<Dictionary<string, bool>>(json, _jsonOptions) ?? new();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载规则开关状态失败");
            _toggleOverrides = new();
        }
    }

    /// <summary>保存配置/系统规则的开关覆盖状态</summary>
    private void SaveToggleOverrides()
    {
        try
        {
            var json = JsonSerializer.Serialize(_toggleOverrides, _jsonOptions);
            File.WriteAllText(ToggleFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存规则开关状态失败");
        }
    }
}
