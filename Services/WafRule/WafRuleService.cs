using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LyWaf.Services.Captcha;
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
    WafRuleCheckResult CheckRequest(HttpContext context, string clientIp);

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

    public WafRuleService(ICaptchaService captchaService, IConfiguration configuration)
    {
        _captchaService = captchaService;
        _configuration = configuration;

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
        if (string.IsNullOrWhiteSpace(rule.Name)) return false;
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

    public WafRuleCheckResult CheckRequest(HttpContext context, string clientIp)
    {
        var result = new WafRuleCheckResult { IsTriggered = false };

        List<WafCustomRule> rules;
        lock (_lock) rules = [.. _mergedRules];

        var enabledRules = rules
            .Where(r => r.Enabled)
            .ToList(); // _mergedRules 已按优先级排好序

        foreach (var rule in enabledRules)
        {
            if (rule.Conditions == null || rule.Conditions.Count == 0) continue;

            // AND: 所有条件都要满足
            var allMatch = true;
            foreach (var condition in rule.Conditions)
            {
                if (!MatchCondition(context, condition, clientIp))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                result.IsTriggered = true;
                result.TriggeredRule = rule;
                result.Action = rule.Action;
                result.Message = $"IP {clientIp} 命中WAF规则 [{rule.Name}] (来源: {rule.Source})";
                _logger.Info("WAF 规则命中: {Message}", result.Message);
                return result;
            }
        }

        return result;
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

    private static bool MatchCondition(HttpContext context, WafCondition condition, string clientIp)
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
            WafMatchField.Cookie => GetCookieValue(request, condition.FieldName),
            WafMatchField.Header => GetHeaderValue(request, condition.FieldName),
            WafMatchField.QueryParam => GetQueryParamValue(request, condition.FieldName),
            WafMatchField.Body => null, // Body 需要特殊处理，暂不支持同步读取
            WafMatchField.ServerPort => context.Connection.LocalPort.ToString(),
            _ => null
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
                _userRules = JsonSerializer.Deserialize<List<WafCustomRule>>(json, _jsonOptions) ?? [];

                // 向后兼容：旧 JSON 可能含 conditionGroups，需手动迁移
                var needsSave = false;
                using var doc = JsonDocument.Parse(json);
                var rulesArr = doc.RootElement;
                for (int i = 0; i < _userRules.Count && i < rulesArr.GetArrayLength(); i++)
                {
                    var rule = _userRules[i];
                    rule.Source = WafRuleSource.User;

                    if ((rule.Conditions == null || rule.Conditions.Count == 0)
                        && rulesArr[i].TryGetProperty("conditionGroups", out var groups))
                    {
                        // 将所有组的条件合并为扁平列表
                        rule.Conditions = [];
                        foreach (var group in groups.EnumerateArray())
                        {
                            if (group.TryGetProperty("conditions", out var conds))
                            {
                                foreach (var c in conds.EnumerateArray())
                                {
                                    var condition = JsonSerializer.Deserialize<WafCondition>(c.GetRawText(), _jsonOptions);
                                    if (condition != null) rule.Conditions.Add(condition);
                                }
                            }
                        }
                        needsSave = true;
                    }
                }
                if (needsSave)
                {
                    _logger.Info("已自动迁移旧版 WAF 规则（conditionGroups → Conditions）");
                    SaveUserRules();
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
    /// config.ly 格式（每条规则是一个命名块）：
    /// <code>
    /// Protect {
    ///     WafRules {
    ///         block-scanners {
    ///             Name = "封禁扫描器"
    ///             Field = UserAgent
    ///             Operator = Regex
    ///             Value = "(?i)(sqlmap|nikto)"
    ///         }
    ///         block-admin-post {
    ///             Name = "拦截 admin POST"
    ///             Conditions = [
    ///                 { Field = UriPath; Operator = StartsWith; Value = "/admin" },
    ///                 { Field = Method;  Operator = Equal;      Value = "POST"   },
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

                // 跳过无效规则：两种条件来源都为空
                var hasConditions = item.Conditions is { Count: > 0 };
                var hasSingleField = !string.IsNullOrWhiteSpace(item.Field);
                if (!hasConditions && !hasSingleField)
                    continue;

                var rule = item.ToRule(child.Key);
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
