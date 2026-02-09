namespace LyWaf.Services.Statistic;

/// <summary>
/// 高级 CC 规则
/// </summary>
public class AdvancedCcRule
{
    /// <summary>
    /// 规则 ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>
    /// 规则名称
    /// </summary>
    public string Name { get; set; } = "";
    
    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// 规则类型
    /// </summary>
    public CcRuleType Type { get; set; } = CcRuleType.FrequentAccess;
    
    /// <summary>
    /// 匹配条件列表（AND 关系）
    /// </summary>
    public List<CcCondition> Conditions { get; set; } = [];
    
    /// <summary>
    /// 统计周期（秒）
    /// </summary>
    public int Period { get; set; } = 10;
    
    /// <summary>
    /// 触发阈值（请求次数）
    /// </summary>
    public int Threshold { get; set; } = 100;
    
    /// <summary>
    /// 触发后的动作
    /// </summary>
    public CcAction Action { get; set; } = CcAction.Captcha;
    
    /// <summary>
    /// 动作持续时间（分钟）
    /// </summary>
    public int ActionDuration { get; set; } = 10;
    
    /// <summary>
    /// 优先级（数值越小优先级越高）
    /// </summary>
    public int Priority { get; set; } = 100;
    
    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// CC 规则类型
/// </summary>
public enum CcRuleType
{
    /// <summary>
    /// 高频访问限制 - 基于请求频率
    /// </summary>
    FrequentAccess,
    
    /// <summary>
    /// 高频攻击限制 - 基于触发 WAF 拦截的频率
    /// </summary>
    FrequentAttack,
    
    /// <summary>
    /// 高频错误限制 - 基于触发错误响应的频率
    /// </summary>
    FrequentError
}

/// <summary>
/// CC 条件
/// </summary>
public class CcCondition
{
    /// <summary>
    /// 匹配目标
    /// </summary>
    public CcMatchTarget Target { get; set; } = CcMatchTarget.UrlPath;
    
    /// <summary>
    /// 匹配方式
    /// </summary>
    public CcMatchOperator Operator { get; set; } = CcMatchOperator.Equal;
    
    /// <summary>
    /// 匹配值（多个值为 OR 关系）
    /// </summary>
    public List<string> Values { get; set; } = [];
}

/// <summary>
/// 匹配目标
/// </summary>
public enum CcMatchTarget
{
    /// <summary>
    /// URL 路径
    /// </summary>
    UrlPath,
    
    /// <summary>
    /// URL 完整地址
    /// </summary>
    FullUrl,
    
    /// <summary>
    /// 请求方法
    /// </summary>
    Method,
    
    /// <summary>
    /// Content-Type
    /// </summary>
    ContentType,
    
    /// <summary>
    /// User-Agent
    /// </summary>
    UserAgent,
    
    /// <summary>
    /// Referer
    /// </summary>
    Referer,
    
    /// <summary>
    /// 自定义请求头
    /// </summary>
    Header,
    
    /// <summary>
    /// 查询参数
    /// </summary>
    QueryParam,
    
    /// <summary>
    /// Cookie
    /// </summary>
    Cookie,
    
    /// <summary>
    /// 客户端 IP
    /// </summary>
    ClientIp,
    
    /// <summary>
    /// 响应状态码（仅用于错误限制）
    /// </summary>
    StatusCode
}

/// <summary>
/// 匹配方式
/// </summary>
public enum CcMatchOperator
{
    /// <summary>
    /// 等于
    /// </summary>
    Equal,
    
    /// <summary>
    /// 不等于
    /// </summary>
    NotEqual,
    
    /// <summary>
    /// 包含
    /// </summary>
    Contains,
    
    /// <summary>
    /// 不包含
    /// </summary>
    NotContains,
    
    /// <summary>
    /// 前缀匹配
    /// </summary>
    StartsWith,
    
    /// <summary>
    /// 后缀匹配
    /// </summary>
    EndsWith,
    
    /// <summary>
    /// 正则匹配
    /// </summary>
    Regex,
    
    /// <summary>
    /// 存在
    /// </summary>
    Exists,
    
    /// <summary>
    /// 不存在
    /// </summary>
    NotExists
}

/// <summary>
/// CC 动作
/// </summary>
public enum CcAction
{
    /// <summary>
    /// 人机验证
    /// </summary>
    Captcha,
    
    /// <summary>
    /// 封禁 IP
    /// </summary>
    Block,
    
    /// <summary>
    /// 直接拒绝
    /// </summary>
    Reject,
    
    /// <summary>
    /// 限速
    /// </summary>
    RateLimit,
    
    /// <summary>
    /// 仅记录（不执行动作）
    /// </summary>
    LogOnly
}
