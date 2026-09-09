namespace LyWaf.Services.WafRule;

/// <summary>
/// 单条 WAF 配置规则，位于 Protect.WafRules 块下。
/// <para>
/// 条件按组配置；Operator 控制组内关系，GroupOperator 控制组间关系。
/// <code>
/// Protect {
///     WafRules {
///         block-admin-post {
///             Name = "拦截 admin POST 请求"
///             Action = Reject
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

    /// <summary>条件组之间的关系：And / Or。</summary>
    public string GroupOperator { get; set; } = "Or";

    /// <summary>条件组，最多 5 组、总计 20 个条件。</summary>
    public List<WafConditionGroupConfigItem> ConditionGroups { get; set; } = [];

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
        Enum.TryParse<WafLogicalOperator>(GroupOperator, true, out var groupOperator);

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
            GroupOperator = groupOperator,
        };

        rule.ConditionGroups = ConditionGroups.Select(group => group.ToGroup()).ToList();

        return rule;
    }
}

public class WafConditionGroupConfigItem
{
    public string Operator { get; set; } = "And";
    public List<WafConditionConfigItem> Conditions { get; set; } = [];

    public WafConditionGroup ToGroup()
    {
        Enum.TryParse<WafLogicalOperator>(Operator, true, out var logicalOperator);
        return new WafConditionGroup
        {
            Operator = logicalOperator,
            Conditions = Conditions.Select(condition => condition.ToCondition()).ToList()
        };
    }
}

/// <summary>
/// 配置条件项（用于条件组中的单条条件）。
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
