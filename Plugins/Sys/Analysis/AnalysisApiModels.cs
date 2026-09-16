using System.ComponentModel.DataAnnotations;

namespace LyWaf.Plugins.Sys.Analysis;

public class AddCcRuleRequest : IValidatableObject
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
    [Range(1, int.MaxValue)]
    public int? Period { get; set; }
    [Range(1, int.MaxValue)]
    public int? LimitNum { get; set; }
    public TimeSpan? FbTime { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FbTime is { } duration && (duration <= TimeSpan.Zero || duration.TotalSeconds > int.MaxValue))
            yield return new ValidationResult("封禁时长必须为正数且不超过支持的秒数范围", [nameof(FbTime)]);
    }
}

public class RemoveCcRuleRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
}

// =============== 高级 CC 规则请求模型 ===============

public class CcConditionRequest
{
    [Required, LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcMatchTarget))]
    public string Target { get; set; } = "UrlPath";
    [Required, LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcMatchOperator))]
    public string Operator { get; set; } = "Equal";
    [MaxLength(100)]
    public List<string>? Values { get; set; }
}

public class AddAdvancedCcRuleRequest
{
    [RegularExpression("^(And|Or)$")]
    public string? GroupOperator { get; set; }
    [MinLength(1), MaxLength(5)]
    [LyWaf.Services.Statistic.CcConditionGroups(ErrorMessage = "条件组必须为 1-5 组，总条件最多 20 条，且条件字段和关系必须有效")]
    public List<LyWaf.Services.Statistic.CcConditionGroup>? ConditionGroups { get; set; }
    [Required, StringLength(200)]
    public string Name { get; set; } = "";
    public bool? Enabled { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcRuleType))]
    public string? Type { get; set; }
    [MaxLength(100)]
    public List<CcConditionRequest>? Conditions { get; set; }
    [Range(1, int.MaxValue)]
    public int? Period { get; set; }
    [Range(1, int.MaxValue)]
    public int? Threshold { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcAction))]
    public string? Action { get; set; }
    [Range(1, int.MaxValue)]
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class UpdateAdvancedCcRuleRequest
{
    [RegularExpression("^(And|Or)$")]
    public string? GroupOperator { get; set; }
    [MinLength(1), MaxLength(5)]
    [LyWaf.Services.Statistic.CcConditionGroups(ErrorMessage = "条件组必须为 1-5 组，总条件最多 20 条，且条件字段和关系必须有效")]
    public List<LyWaf.Services.Statistic.CcConditionGroup>? ConditionGroups { get; set; }
    [Required, StringLength(128)]
    public string Id { get; set; } = "";
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcRuleType))]
    public string? Type { get; set; }
    [MaxLength(100)]
    public List<CcConditionRequest>? Conditions { get; set; }
    [Range(1, int.MaxValue)]
    public int? Period { get; set; }
    [Range(1, int.MaxValue)]
    public int? Threshold { get; set; }
    [LyWaf.Services.ManagementApi.EnumName(typeof(LyWaf.Services.Statistic.CcAction))]
    public string? Action { get; set; }
    [Range(1, int.MaxValue)]
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class RemoveAdvancedCcRuleRequest
{
    [Required, StringLength(128)]
    public string RuleId { get; set; } = "";
}

public class ToggleAdvancedCcRuleRequest
{
    [Required, StringLength(128)]
    public string RuleId { get; set; } = "";
    public bool? Enabled { get; set; }
}

public class PathRequest
{
    [Required, StringLength(8192)]
    public string Path { get; set; } = "";
}


