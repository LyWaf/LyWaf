namespace LyWaf.Services.LyLog;

/// <summary>
/// 域名日志配置
/// </summary>
public class LyLogOptions
{
    /// <summary>
    /// 是否启用域名日志
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 全局日志配置
    /// </summary>
    public GlobalLogConfig Global { get; set; } = new();

    /// <summary>
    /// 按域名的日志配置
    /// Key: 域名（如 example.com，支持 *.example.com 通配符）
    /// Value: 该域名的日志配置
    /// </summary>
    public Dictionary<string, LyLogConfig> Domains { get; set; } = [];
}

/// <summary>
/// 全局日志配置
/// </summary>
public class GlobalLogConfig
{
    /// <summary>
    /// 是否启用全局日志（所有域名的请求都记录到全局日志）
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 全局访问日志文件名（支持 NLog 变量如 ${shortdate}）
    /// </summary>
    public string AccessLog { get; set; } = "access_${shortdate}.log";

    /// <summary>
    /// 全局错误日志文件名
    /// </summary>
    public string ErrorLog { get; set; } = "error_${shortdate}.log";

    /// <summary>
    /// 日志目录
    /// </summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// 日志级别: Trace, Debug, Info, Warn, Error, Fatal
    /// </summary>
    public string Level { get; set; } = "Info";

    /// <summary>
    /// 日志格式: Text 或 Json
    /// </summary>
    public LogFormat Format { get; set; } = LogFormat.Text;

    /// <summary>
    /// 高性能日志（保持文件打开）
    /// </summary>
    public bool PerfLog { get; set; } = false;
}

/// <summary>
/// 日志格式
/// </summary>
public enum LogFormat
{
    /// <summary>
    /// 文本格式
    /// </summary>
    Text,

    /// <summary>
    /// JSON 格式
    /// </summary>
    Json
}

/// <summary>
/// 单个域名的日志配置
/// </summary>
public class LyLogConfig
{
    /// <summary>
    /// 是否启用该域名的日志
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 日志输出目录（相对路径或绝对路径）
    /// 如果为空，则使用 logs/{domain}
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// 访问日志文件名（默认 access_${shortdate}.log）
    /// </summary>
    public string AccessLog { get; set; } = "access_${shortdate}.log";

    /// <summary>
    /// 错误日志文件名（默认 error_${shortdate}.log）
    /// </summary>
    public string ErrorLog { get; set; } = "error_${shortdate}.log";

    /// <summary>
    /// 日志级别（覆盖全局设置）
    /// </summary>
    public string? Level { get; set; }

    /// <summary>
    /// 日志格式（覆盖全局设置）
    /// </summary>
    public LogFormat? Format { get; set; }

    /// <summary>
    /// 是否同时记录到全局日志
    /// </summary>
    public bool AlsoLogToGlobal { get; set; } = true;

    /// <summary>
    /// 排除的路径（不记录日志）
    /// </summary>
    public List<string> ExcludePaths { get; set; } = [];

    /// <summary>
    /// 仅记录的路径（如果设置，只记录这些路径）
    /// </summary>
    public List<string> IncludePaths { get; set; } = [];
}
