using System.ComponentModel.DataAnnotations;

namespace LyWaf.Services.Statistic;

/// <summary>Conditions combined with a shared AND/OR relationship, matching the WAF group model.</summary>
public sealed class CcConditionGroup
{
    [Required, RegularExpression("^(And|Or)$")]
    public string Operator { get; set; } = "And";
    [Required, MinLength(1), MaxLength(20)]
    public List<CcCondition> Conditions { get; set; } = [];
}

/// <summary>Limits the complete editor expression and rejects invalid enum values before persistence.</summary>
public sealed class CcConditionGroupsAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        if (value is not List<CcConditionGroup> groups || groups.Count is < 1 or > 5) return false;
        return groups.All(g => g != null && g.Operator is "And" or "Or" && g.Conditions is { Count: > 0 } &&
            g.Conditions.All(c => c != null && Enum.IsDefined(c.Target) && Enum.IsDefined(c.Operator) && c.Values != null &&
                c.Values.Count <= 100 && c.Values.All(v => v != null && v.Length <= 8192))) &&
            groups.Sum(g => g.Conditions.Count) <= 20;
    }
}
