using System.Text.Json.Serialization;
using LyWaf.Services.WafRule;

namespace LyWaf.Control;

/// <summary>管理端规则视图，显式包含运行时来源，不改变规则持久化模型。</summary>
public sealed record WafRuleView(string Id, string Name, string Description, bool Enabled, int Priority,
    string Source, string GroupOperator, WafConditionGroupView[] ConditionGroups, string Action,
    int ActionSeconds, int ResponseCode, DateTime CreatedAt, DateTime? UpdatedAt)
{
    public static WafRuleView From(WafCustomRule rule) => new(rule.Id, rule.Name, rule.Description,
        rule.Enabled, rule.Priority, rule.Source.ToString(), rule.GroupOperator.ToString(),
        rule.ConditionGroups.Select(group => new WafConditionGroupView(group.Operator.ToString(),
            group.Conditions.Select(condition => new WafConditionView(condition.Field.ToString(),
                condition.FieldName, condition.Operator.ToString(), condition.Value, condition.IgnoreCase)).ToArray())).ToArray(),
        rule.Action.ToString(), rule.ActionSeconds, rule.ResponseCode, rule.CreatedAt, rule.UpdatedAt);
}

public sealed record WafConditionGroupView([property: JsonPropertyName("operator")] string Operator, WafConditionView[] Conditions);
public sealed record WafConditionView(string Field, string? FieldName,
    [property: JsonPropertyName("operator")] string Operator, string Value, bool IgnoreCase);
public sealed record WafRuleDetailResponse(bool Success, WafRuleView Rule);
public sealed record WafRuleSavedResponse(bool Success, string Message, string? Id = null);
public sealed record WafRuleToggleResponse(bool Success, bool? Enabled, string Message);
public sealed record WafEnumOption(string Name, string Label);
public sealed record WafRuleEnumsResponse(bool Success, WafEnumOption[] Fields, WafEnumOption[] Operators,
    WafEnumOption[] Actions, WafEnumOption[] Sources);
