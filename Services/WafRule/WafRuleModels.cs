using System.Text.Json.Serialization;

namespace LyWaf.Services.WafRule;

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

/// <summary>
/// WAF 匹配条件
/// </summary>
public class WafCondition
{
    /// <summary>匹配字段</summary>
    public WafMatchField Field { get; set; } = WafMatchField.UriPath;

    /// <summary>字段名称（Header/Cookie/QueryParam 时需指定）</summary>
    public string? FieldName { get; set; }

    /// <summary>匹配操作符</summary>
    public WafMatchOperator Operator { get; set; } = WafMatchOperator.Contains;

    /// <summary>匹配值</summary>
    public string Value { get; set; } = "";

    /// <summary>忽略大小写</summary>
    public bool IgnoreCase { get; set; } = true;
}

/// <summary>
/// WAF 条件组（组内 AND 关系）
/// </summary>
public class WafConditionGroup
{
    /// <summary>组内条件列表（AND 关系，全部满足才算该组匹配）</summary>
    public List<WafCondition> Conditions { get; set; } = [];
}

/// <summary>
/// WAF 自定义规则
/// </summary>
public class WafCustomRule
{
    /// <summary>规则 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>规则名称</summary>
    public string Name { get; set; } = "";

    /// <summary>规则描述</summary>
    public string Description { get; set; } = "";

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>优先级（数值越小越高）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>规则来源（运行时计算，不持久化）</summary>
    [JsonIgnore]
    public WafRuleSource Source { get; set; } = WafRuleSource.User;

    /// <summary>条件组列表（组间 OR 关系，任一组匹配即触发）</summary>
    public List<WafConditionGroup> ConditionGroups { get; set; } = [];

    /// <summary>（旧版兼容）平铺条件列表，加载时自动迁移到 ConditionGroups</summary>
    public List<WafCondition>? Conditions { get; set; }

    /// <summary>触发动作</summary>
    public WafRuleAction Action { get; set; } = WafRuleAction.Observe;

    /// <summary>封禁持续时间（秒）</summary>
    public int ActionSeconds { get; set; } = 600;

    /// <summary>拒绝时的响应码</summary>
    public int ResponseCode { get; set; } = 403;

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>更新时间</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// 迁移旧版 Conditions 到 ConditionGroups（调用后清除旧字段）
    /// </summary>
    public void MigrateFromLegacy()
    {
        if (Conditions is { Count: > 0 } && ConditionGroups.Count == 0)
        {
            ConditionGroups.Add(new WafConditionGroup { Conditions = Conditions });
        }
        Conditions = null;
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
