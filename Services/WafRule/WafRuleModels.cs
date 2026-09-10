using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LyWaf.Services.WafRule;

/// <summary>命中条件的规则内位置及请求字段，不包含字段值或敏感内容。</summary>
public sealed record WafMatchLocation(int Group, int Condition, string Field, string? FieldName, string Operator);

/// <summary>
/// WAF 匹配字段
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafMatchField
{
    /// <summary>URL 路径</summary>
    UriPath,
    /// <summary>完整 URL</summary>
    FullUrl,
    /// <summary>查询字符串</summary>
    QueryString,
    /// <summary>HTTP 方法</summary>
    Method,
    /// <summary>客户端 IP</summary>
    ClientIp,
    /// <summary>X-Forwarded-For</summary>
    XForwardedFor,
    /// <summary>User-Agent</summary>
    UserAgent,
    /// <summary>Referer</summary>
    Referer,
    /// <summary>Content-Type</summary>
    ContentType,
    /// <summary>Content-Length</summary>
    ContentLength,
    /// <summary>指定 Cookie（需 FieldName）</summary>
    Cookie,
    /// <summary>指定请求头（需 FieldName）</summary>
    Header,
    /// <summary>指定查询参数（需 FieldName）</summary>
    QueryParam,
    /// <summary>请求体</summary>
    Body,
    /// <summary>服务端口</summary>
    ServerPort,
}

/// <summary>
/// WAF 匹配操作符
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafMatchOperator
{
    /// <summary>等于</summary>
    Equal,
    /// <summary>不等于</summary>
    NotEqual,
    /// <summary>包含</summary>
    Contains,
    /// <summary>不包含</summary>
    NotContains,
    /// <summary>前缀匹配</summary>
    StartsWith,
    /// <summary>后缀匹配</summary>
    EndsWith,
    /// <summary>正则匹配</summary>
    Regex,
    /// <summary>存在</summary>
    Exists,
    /// <summary>不存在</summary>
    NotExists,
    /// <summary>长度大于</summary>
    LengthGreaterThan,
    /// <summary>长度小于</summary>
    LengthLessThan,
}

/// <summary>
/// WAF 规则动作
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafRuleAction
{
    /// <summary>仅观察记录</summary>
    Observe,
    /// <summary>封禁 IP</summary>
    Block,
    /// <summary>直接拒绝</summary>
    Reject,
    /// <summary>人机验证</summary>
    Captcha,
}

/// <summary>
/// WAF 规则来源
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafRuleSource
{
    /// <summary>用户创建（管理面板）</summary>
    User,
    /// <summary>配置文件（config.ly）</summary>
    Config,
    /// <summary>系统内置规则</summary>
    System,
}

/// <summary>条件或条件组之间的逻辑关系。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafLogicalOperator
{
    And,
    Or,
}

/// <summary>
/// WAF 匹配条件
/// </summary>
public class WafCondition : IValidatableObject
{
    /// <summary>匹配字段</summary>
    public WafMatchField Field { get; set; } = WafMatchField.UriPath;

    /// <summary>字段名称（Header/Cookie/QueryParam 时需指定）</summary>
    [StringLength(1024)]
    public string? FieldName { get; set; }

    /// <summary>匹配操作符</summary>
    public WafMatchOperator Operator { get; set; } = WafMatchOperator.Contains;

    /// <summary>匹配值</summary>
    [StringLength(32768)]
    public string Value { get; set; } = "";

    /// <summary>忽略大小写</summary>
    public bool IgnoreCase { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Field is WafMatchField.Header or WafMatchField.Cookie or WafMatchField.QueryParam && string.IsNullOrWhiteSpace(FieldName))
            yield return new ValidationResult("此匹配字段需要指定名称", [nameof(FieldName)]);
        if (Operator is WafMatchOperator.LengthGreaterThan or WafMatchOperator.LengthLessThan &&
            (!int.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0))
            yield return new ValidationResult("长度必须为非负整数", [nameof(Value)]);
        if (Operator == WafMatchOperator.Regex)
        {
            var valid = true;
            try { _ = new Regex(Value ?? "", RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { valid = false; }
            if (!valid) yield return new ValidationResult("正则表达式无效", [nameof(Value)]);
        }
    }
}

/// <summary>一组具有相同逻辑关系的 WAF 匹配条件。</summary>
public class WafConditionGroup
{
    /// <summary>组内条件关系。</summary>
    public WafLogicalOperator Operator { get; set; } = WafLogicalOperator.And;

    /// <summary>组内条件。</summary>
    [Required, MinLength(1), MaxLength(20)]
    public List<WafCondition> Conditions { get; set; } = [];
}

/// <summary>
/// WAF 自定义规则
/// </summary>
public class WafCustomRule : IValidatableObject
{
    /// <summary>规则 ID</summary>
    [Required, StringLength(128)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>规则名称</summary>
    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    /// <summary>规则描述</summary>
    [StringLength(4096)]
    public string Description { get; set; } = "";

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>优先级（数值越小越高）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>规则来源（运行时计算，不持久化）</summary>
    [JsonIgnore]
    public WafRuleSource Source { get; set; } = WafRuleSource.User;

    /// <summary>条件组之间的逻辑关系。</summary>
    public WafLogicalOperator GroupOperator { get; set; } = WafLogicalOperator.Or;

    /// <summary>分组条件。</summary>
    [Required, MinLength(1), MaxLength(5)]
    public List<WafConditionGroup> ConditionGroups { get; set; } = [];

    /// <summary>触发动作</summary>
    public WafRuleAction Action { get; set; } = WafRuleAction.Observe;

    /// <summary>封禁持续时间（秒）</summary>
    [Range(1, int.MaxValue)]
    public int ActionSeconds { get; set; } = 600;

    /// <summary>拒绝时的响应码</summary>
    [Range(400, 599)]
    public int ResponseCode { get; set; } = 403;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>更新时间</summary>
    public DateTime? UpdatedAt { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ConditionGroups?.Sum(group => group?.Conditions?.Count ?? 0) > 20)
            yield return new ValidationResult("条件总数不能超过 20 个", [nameof(ConditionGroups)]);
    }
}

/// <summary>
/// WAF 规则检查结果
/// </summary>
public class WafRuleCheckResult
{
    public bool IsTriggered { get; set; }
    public WafCustomRule? TriggeredRule { get; set; }
    public WafRuleAction Action { get; set; }
    public string? Message { get; set; }
}
