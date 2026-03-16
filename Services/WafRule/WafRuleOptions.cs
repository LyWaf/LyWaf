namespace LyWaf.Services.WafRule;

/// <summary>
/// 单条 WAF 配置规则（简化格式），位于 Protect.WafRules 块下
/// <para>
/// ■ 单条件简写（最常用）：
/// <code>
/// Protect {
///     WafRules {
///         block-git {
///             Name = "拦截 .git 访问"
///             Field = UriPath
///             Value = "/.git/"
///         }
///     }
/// }
/// </code>
/// 默认：Operator = Contains, Action = Reject, IgnoreCase = true
/// </para>
/// <para>
/// ■ 多条件 AND（Conditions 数组，全部满足才触发）：
/// <code>
/// Protect {
///     WafRules {
///         block-admin-post {
///             Name = "拦截 admin POST 请求"
///             Action = Reject
///             Conditions = [
///                 { Field = UriPath;  Operator = StartsWith; Value = "/admin" },
///                 { Field = Method;   Operator = Equal;      Value = "POST"   },
///             ]
///         }
///     }
/// }
/// </code>
/// </para>
/// </summary>
public class WafRuleConfigItem
{
    /// <summary>规则名称（不填则用 block 名）</summary>
    public string Name { get; set; } = "";

    /// <summary>规则描述</summary>
    public string Description { get; set; } = "";

    /// <summary>是否启用（默认 true）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>优先级，数值越小越高（默认 100）</summary>
    public int Priority { get; set; } = 100;

    // ── 单条件简写（最简用法）──

    /// <summary>匹配字段：UriPath / QueryString / Method / ClientIp / UserAgent 等</summary>
    public string? Field { get; set; }

    /// <summary>匹配操作符（默认 Contains）</summary>
    public string Operator { get; set; } = "Contains";

    /// <summary>匹配值</summary>
    public string? Value { get; set; }

    /// <summary>字段名称（仅 Header / Cookie / QueryParam 时需要）</summary>
    public string? FieldName { get; set; }

    /// <summary>忽略大小写（默认 true）</summary>
    public bool IgnoreCase { get; set; } = true;

    // ── 多条件写法 ──

    /// <summary>
    /// 多条件列表（AND 关系，优先于单条件简写）
    /// <para>config.ly: Conditions = [{ Field=...; Value=... }, { ... }]</para>
    /// </summary>
    public List<WafConditionConfigItem>? Conditions { get; set; }

    // ── 动作 ──

    /// <summary>触发动作：Observe / Block / Reject / Captcha（默认 Reject）</summary>
    public string Action { get; set; } = "Reject";

    /// <summary>封禁/验证持续秒数（默认 600）</summary>
    public int ActionSeconds { get; set; } = 600;

    /// <summary>拒绝响应码（默认 403）</summary>
    public int ResponseCode { get; set; } = 403;

    /// <summary>转换为 WafCustomRule</summary>
    public WafCustomRule ToRule(string configKey)
    {
        Enum.TryParse<WafRuleAction>(Action, true, out var action);

        var rule = new WafCustomRule
        {
            Id = configKey.StartsWith("cfg-") ? configKey : $"cfg-{configKey}",
            Name = string.IsNullOrWhiteSpace(Name) ? configKey : Name,
            Description = Description,
            Enabled = Enabled,
            Priority = Priority,
            Source = WafRuleSource.Config,
            Action = action,
            ActionSeconds = ActionSeconds,
            ResponseCode = ResponseCode,
        };

        if (Conditions is { Count: > 0 })
        {
            // 多条件 AND
            foreach (var c in Conditions)
                rule.Conditions.Add(c.ToCondition());
        }
        else if (!string.IsNullOrWhiteSpace(Field))
        {
            // 单条件简写
            Enum.TryParse<WafMatchField>(Field, true, out var field);
            Enum.TryParse<WafMatchOperator>(Operator, true, out var op);
            rule.Conditions.Add(new WafCondition
            {
                Field = field,
                FieldName = FieldName,
                Operator = op,
                Value = Value ?? "",
                IgnoreCase = IgnoreCase,
            });
        }

        return rule;
    }
}

/// <summary>
/// 配置条件项（用于 Conditions 列表中的单条条件）
/// </summary>
public class WafConditionConfigItem
{
    public string Field { get; set; } = "UriPath";
    public string Operator { get; set; } = "Contains";
    public string Value { get; set; } = "";
    public string? FieldName { get; set; }
    public bool IgnoreCase { get; set; } = true;

    public WafCondition ToCondition()
    {
        Enum.TryParse<WafMatchField>(Field, true, out var field);
        Enum.TryParse<WafMatchOperator>(Operator, true, out var op);
        return new WafCondition
        {
            Field = field,
            FieldName = FieldName,
            Operator = op,
            Value = Value,
            IgnoreCase = IgnoreCase,
        };
    }
}
