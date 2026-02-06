namespace LyWaf.Services.ErrorTemplate;

/// <summary>
/// 错误模板配置
/// </summary>
public class ErrorTemplateOptions
{
    /// <summary>
    /// 是否启用自定义错误模板
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 是否在错误页面显示详细原因（生产环境建议关闭）
    /// </summary>
    public bool ShowReason { get; set; } = false;

    /// <summary>
    /// 403 Forbidden 模板配置
    /// </summary>
    public ErrorTemplateConfig Forbidden { get; set; } = new();

    /// <summary>
    /// 404 Not Found 模板配置
    /// </summary>
    public ErrorTemplateConfig NotFound { get; set; } = new();

    /// <summary>
    /// 429 Too Many Requests 模板配置（CC防护触发）
    /// </summary>
    public ErrorTemplateConfig TooManyRequests { get; set; } = new();

    /// <summary>
    /// 500 Internal Server Error 模板配置
    /// </summary>
    public ErrorTemplateConfig InternalError { get; set; } = new();

    /// <summary>
    /// 502 Bad Gateway 模板配置
    /// </summary>
    public ErrorTemplateConfig BadGateway { get; set; } = new();

    /// <summary>
    /// 503 Service Unavailable 模板配置
    /// </summary>
    public ErrorTemplateConfig ServiceUnavailable { get; set; } = new();

    /// <summary>
    /// 自定义状态码模板配置
    /// Key: 状态码（如 "401", "418" 等）
    /// </summary>
    public Dictionary<string, ErrorTemplateConfig> Custom { get; set; } = [];
}

/// <summary>
/// 单个错误模板配置
/// </summary>
public class ErrorTemplateConfig
{
    /// <summary>
    /// 模板类型: File（文件）、Inline（内联字符串）、Default（默认）
    /// </summary>
    public TemplateType Type { get; set; } = TemplateType.Default;

    /// <summary>
    /// 模板文件路径（当 Type 为 File 时使用）
    /// 支持相对路径和绝对路径
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 内联模板内容（当 Type 为 Inline 时使用）
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Content-Type 响应头
    /// </summary>
    public string ContentType { get; set; } = "text/html; charset=utf-8";

    /// <summary>
    /// 额外的响应头
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];
}

/// <summary>
/// 模板类型
/// </summary>
public enum TemplateType
{
    /// <summary>
    /// 使用默认模板
    /// </summary>
    Default,

    /// <summary>
    /// 从文件加载模板
    /// </summary>
    File,

    /// <summary>
    /// 使用内联字符串模板
    /// </summary>
    Inline
}
