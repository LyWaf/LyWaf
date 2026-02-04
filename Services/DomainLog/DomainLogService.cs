using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LyWaf.Services.DomainLog;

/// <summary>
/// 域名日志服务接口
/// </summary>
public interface IDomainLogService
{
    /// <summary>
    /// 获取指定域名的 Logger
    /// </summary>
    Logger GetLogger(string domain);

    /// <summary>
    /// 获取全局 Logger
    /// </summary>
    Logger GetGlobalLogger();

    /// <summary>
    /// 获取指定域名的日志格式
    /// </summary>
    LogFormat GetFormat(string domain);

    /// <summary>
    /// 记录 Info 日志（根据格式自动处理）
    /// 如果是 Json 格式，输出 {"msg": "消息", "time": "时间"}
    /// 如果是 Text 格式，输出普通文本
    /// </summary>
    void LogInfo(string domain, string message, params object[] args);

    /// <summary>
    /// 记录 Json 日志
    /// 如果是 Json 格式，添加 time 字段后输出
    /// 如果是 Text 格式，直接将字典序列化为 JSON 输出
    /// </summary>
    void LogJson(string domain, Dictionary<string, object> data);

    /// <summary>
    /// 获取指定域名的配置
    /// </summary>
    DomainLogConfig? GetDomainConfig(string domain);

    /// <summary>
    /// 动态添加域名日志配置
    /// </summary>
    void AddDomainConfig(string domain, DomainLogConfig config);

    /// <summary>
    /// 移除域名日志配置
    /// </summary>
    void RemoveDomainConfig(string domain);

    /// <summary>
    /// 获取所有配置的域名
    /// </summary>
    IEnumerable<string> GetConfiguredDomains();

    /// <summary>
    /// 检查路径是否应该被记录
    /// </summary>
    bool ShouldLog(string domain, string path);
}

/// <summary>
/// 域名日志服务实现
/// </summary>
public class DomainLogService : IDomainLogService
{
    private readonly DomainLogOptions _options;
    private readonly string _projectRoot;
    private readonly ConcurrentDictionary<string, Logger> _domainLoggers = new();
    private readonly Logger _globalLogger;
    private readonly ConcurrentDictionary<string, DomainLogConfig> _dynamicConfigs = new();

    public DomainLogService(IOptions<DomainLogOptions> options, string? projectRoot = null)
    {
        _options = options.Value;
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();

        // 配置日志目标
        ConfigureLogging();

        // 获取全局日志
        _globalLogger = LogManager.GetLogger("DomainLog.Global");
    }

    private void ConfigureLogging()
    {
        var config = LogManager.Configuration ?? new LoggingConfiguration();

        // 配置全局日志目标
        if (_options.Global.Enabled)
        {
            var globalDir = Path.Combine(_projectRoot, _options.Global.Directory);
            Directory.CreateDirectory(globalDir);
            var isJson = _options.Global.Format == LogFormat.Json;

            var globalTarget = CreateFileTarget(
                "domainlog-global",
                Path.Combine(globalDir, _options.Global.AccessLog),
                _options.Global.PerfLog,
                isJson
            );
            config.AddTarget(globalTarget);
            config.AddRule(ParseLogLevel(_options.Global.Level), NLog.LogLevel.Fatal, globalTarget, "DomainLog.Global");

            var globalErrorTarget = CreateFileTarget(
                "domainlog-global-error",
                Path.Combine(globalDir, _options.Global.ErrorLog),
                _options.Global.PerfLog,
                isJson
            );
            config.AddTarget(globalErrorTarget);
            config.AddRuleForOneLevel(NLog.LogLevel.Error, globalErrorTarget, "DomainLog.Global");
        }

        // 配置各域名日志目标
        foreach (var (domain, domainConfig) in _options.Domains)
        {
            ConfigureDomainLogging(config, domain, domainConfig);
        }

        LogManager.Configuration = config;
    }

    private void ConfigureDomainLogging(LoggingConfiguration config, string domain, DomainLogConfig domainConfig)
    {
        if (!domainConfig.Enabled) return;

        var domainDir = GetDomainLogDirectory(domain, domainConfig);
        Directory.CreateDirectory(domainDir);

        var loggerName = $"DomainLog.{SanitizeDomain(domain)}";
        var level = domainConfig.Level ?? _options.Global.Level;
        var isJson = (domainConfig.Format ?? _options.Global.Format) == LogFormat.Json;

        // 访问日志
        var accessTarget = CreateFileTarget(
            $"domainlog-{SanitizeDomain(domain)}",
            Path.Combine(domainDir, domainConfig.AccessLog),
            _options.Global.PerfLog,
            isJson
        );
        config.AddTarget(accessTarget);
        config.AddRule(ParseLogLevel(level), NLog.LogLevel.Fatal, accessTarget, loggerName);

        // 错误日志
        var errorTarget = CreateFileTarget(
            $"domainlog-{SanitizeDomain(domain)}-error",
            Path.Combine(domainDir, domainConfig.ErrorLog),
            _options.Global.PerfLog,
            isJson
        );
        config.AddTarget(errorTarget);
        config.AddRuleForOneLevel(NLog.LogLevel.Error, errorTarget, loggerName);

        // 如果需要同时记录到全局日志
        if (domainConfig.AlsoLogToGlobal && _options.Global.Enabled)
        {
            var globalTarget = config.FindTargetByName("domainlog-global");
            if (globalTarget != null)
            {
                config.AddRule(ParseLogLevel(level), NLog.LogLevel.Fatal, globalTarget, loggerName);
            }
        }
    }

    private string GetDomainLogDirectory(string domain, DomainLogConfig config)
    {
        if (!string.IsNullOrEmpty(config.Output))
        {
            return Path.IsPathRooted(config.Output)
                ? config.Output
                : Path.Combine(_projectRoot, config.Output);
        }
        return Path.Combine(_projectRoot, "logs", SanitizeDomain(domain));
    }

    private FileTarget CreateFileTarget(string name, string fileName, bool perfLog, bool isJson = false)
    {
        // 替换 # 为 $ (NLog 变量)
        fileName = fileName.Replace("#", "$");

        return new FileTarget(name)
        {
            FileName = fileName,
            ArchiveFileName = Path.Combine(Path.GetDirectoryName(fileName)!, "archive", "{#}.log"),
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveFiles = 30,
            // Json 格式只需要 ${message}，因为消息本身已包含时间、级别等信息
            Layout = isJson ? "${message}" : "${longdate} ${level:uppercase=true:padding=-5} ${message} ${exception:format=tostring}",
            KeepFileOpen = perfLog,
            Encoding = Encoding.UTF8
        };
    }

    private static NLog.LogLevel ParseLogLevel(string level)
    {
        return level.ToLower() switch
        {
            "trace" => NLog.LogLevel.Trace,
            "debug" => NLog.LogLevel.Debug,
            "info" => NLog.LogLevel.Info,
            "warn" or "warning" => NLog.LogLevel.Warn,
            "error" => NLog.LogLevel.Error,
            "fatal" => NLog.LogLevel.Fatal,
            _ => NLog.LogLevel.Info
        };
    }

    private static string SanitizeDomain(string domain)
    {
        // 将域名转换为安全的 Logger 名称
        return domain.Replace(".", "_").Replace(":", "_").Replace("*", "wildcard");
    }

    public Logger GetLogger(string domain)
    {
        // 先尝试精确匹配
        var config = GetDomainConfig(domain);
        if (config != null && config.Enabled)
        {
            return _domainLoggers.GetOrAdd(domain, d =>
            {
                var loggerName = $"DomainLog.{SanitizeDomain(d)}";
                return LogManager.GetLogger(loggerName);
            });
        }

        // 如果没有配置该域名，返回全局 Logger
        return _globalLogger;
    }

    public Logger GetGlobalLogger()
    {
        return _globalLogger;
    }

    public LogFormat GetFormat(string domain)
    {
        var config = GetDomainConfig(domain);
        return config?.Format ?? _options.Global.Format;
    }

    public void LogInfo(string domain, string message, params object[] args)
    {
        var logger = GetLogger(domain);
        var format = GetFormat(domain);
        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;

        if (format == LogFormat.Json)
        {
            // Json 格式：输出 {"msg": "消息", "time": "时间"}
            var jsonObj = new Dictionary<string, object>
            {
                ["msg"] = formattedMessage,
                ["level"] = "Info",
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            };
            var jsonStr = JsonSerializer.Serialize(jsonObj, _jsonOptions);
            logger.Info(jsonStr);
        }
        else
        {
            // Text 格式：直接输出
            logger.Info(formattedMessage);
        }
    }

    public void LogJson(string domain, Dictionary<string, object> data)
    {
        var logger = GetLogger(domain);

        // 添加 time 字段
        data["time"] = "Info";
        data["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        // 无论什么格式，都序列化为 JSON 输出
        var jsonStr = JsonSerializer.Serialize(data, _jsonOptions);
        logger.Info(jsonStr);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public DomainLogConfig? GetDomainConfig(string domain)
    {
        // 先查动态配置
        if (_dynamicConfigs.TryGetValue(domain, out var dynamicConfig))
            return dynamicConfig;

        // 再查静态配置（精确匹配）
        if (_options.Domains.TryGetValue(domain, out var config))
            return config;

        // 尝试通配符匹配 *.example.com
        foreach (var (pattern, cfg) in _options.Domains.Concat(_dynamicConfigs))
        {
            if (pattern.StartsWith("*.") && domain.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return cfg;
        }

        return null;
    }

    public bool ShouldLog(string domain, string path)
    {
        var config = GetDomainConfig(domain);
        if (config == null)
        {
            // 没有配置，使用全局日志（如果启用）
            return _options.Global.Enabled;
        }

        if (!config.Enabled)
            return false;

        // 排除路径检查
        if (config.ExcludePaths.Count > 0 &&
            config.ExcludePaths.Any(p => MatchPath(path, p)))
        {
            return false;
        }

        // 包含路径检查
        if (config.IncludePaths.Count > 0 &&
            !config.IncludePaths.Any(p => MatchPath(path, p)))
        {
            return false;
        }

        return true;
    }

    private static bool MatchPath(string path, string pattern)
    {
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    public void AddDomainConfig(string domain, DomainLogConfig config)
    {
        _dynamicConfigs[domain] = config;

        // 动态添加 NLog 配置
        var nlogConfig = LogManager.Configuration ?? new LoggingConfiguration();
        ConfigureDomainLogging(nlogConfig, domain, config);
        LogManager.Configuration = nlogConfig;
    }

    public void RemoveDomainConfig(string domain)
    {
        _dynamicConfigs.TryRemove(domain, out _);
        _domainLoggers.TryRemove(domain, out _);
    }

    public IEnumerable<string> GetConfiguredDomains()
    {
        return _options.Domains.Keys.Concat(_dynamicConfigs.Keys).Distinct();
    }
}

/// <summary>
/// HttpContext 扩展方法 - 用于获取和设置域名 Logger
/// </summary>
public static class DomainLogExtensions
{
    private const string LoggerKey = "DomainLogger";
    private const string DomainKey = "LogDomain";
    private const string FormatKey = "LogFormat";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 设置当前请求的域名 Logger
    /// </summary>
    public static void SetDomainLogger(this HttpContext context, Logger logger, string domain, LogFormat format = LogFormat.Text)
    {
        context.Items[LoggerKey] = logger;
        context.Items[DomainKey] = domain;
        context.Items[FormatKey] = format;
    }

    /// <summary>
    /// 获取当前请求的域名 Logger
    /// </summary>
    public static Logger? GetDomainLogger(this HttpContext context)
    {
        return context.Items.TryGetValue(LoggerKey, out var logger) ? logger as Logger : null;
    }

    /// <summary>
    /// 获取当前请求的日志域名
    /// </summary>
    public static string? GetLogDomain(this HttpContext context)
    {
        return context.Items.TryGetValue(DomainKey, out var domain) ? domain as string : null;
    }

    /// <summary>
    /// 获取当前请求的日志格式
    /// </summary>
    public static LogFormat GetLogFormat(this HttpContext context)
    {
        return context.Items.TryGetValue(FormatKey, out var format) && format is LogFormat f ? f : LogFormat.Text;
    }

    /// <summary>
    /// 格式化日志消息（根据配置的格式）
    /// </summary>
    private static string FormatMessage(HttpContext context, string level, string message, params object[] args)
    {
        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
        var format = context.GetLogFormat();

        if (format == LogFormat.Json)
        {
            var jsonObj = new Dictionary<string, object>
            {
                ["level"] = level,
                ["msg"] = formattedMessage,
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            };
            return JsonSerializer.Serialize(jsonObj, _jsonOptions);
        }

        return formattedMessage;
    }

    /// <summary>
    /// 使用域名 Logger 记录 Info 日志（如果存在）
    /// </summary>
    public static void LogDomainInfo(this HttpContext context, string message, params object[] args)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        var msg = FormatMessage(context, "INFO", message, args);
        logger.Info(msg);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Debug 日志（如果存在）
    /// </summary>
    public static void LogDomainDebug(this HttpContext context, string message, params object[] args)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        var msg = FormatMessage(context, "DEBUG", message, args);
        logger.Debug(msg);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Warn 日志（如果存在）
    /// </summary>
    public static void LogDomainWarn(this HttpContext context, string message, params object[] args)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        var msg = FormatMessage(context, "WARN", message, args);
        logger.Warn(msg);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Error 日志（如果存在）
    /// </summary>
    public static void LogDomainError(this HttpContext context, string message, params object[] args)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        var msg = FormatMessage(context, "ERROR", message, args);
        logger.Error(msg);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Error 日志（如果存在）
    /// </summary>
    public static void LogDomainError(this HttpContext context, Exception ex, string message, params object[] args)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        var format = context.GetLogFormat();
        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;

        if (format == LogFormat.Json)
        {
            var jsonObj = new Dictionary<string, object>
            {
                ["level"] = "ERROR",
                ["msg"] = formattedMessage,
                ["error"] = ex.Message,
                ["stack"] = ex.StackTrace ?? "",
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            };
            logger.Error(JsonSerializer.Serialize(jsonObj, _jsonOptions));
        }
        else
        {
            logger.Error(ex, formattedMessage);
        }
    }

    /// <summary>
    /// 使用域名 Logger 记录 Json 日志（自动添加 time 字段，默认 Info 级别）
    /// </summary>
    public static void LogDomainJson(this HttpContext context, Dictionary<string, object> data)
    {
        context.LogDomainJsonInfo(data);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Json Info 日志（自动添加 level 和 time 字段）
    /// </summary>
    public static void LogDomainJsonInfo(this HttpContext context, Dictionary<string, object> data)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        data["level"] = "INFO";
        data["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var jsonStr = JsonSerializer.Serialize(data, _jsonOptions);
        logger.Info(jsonStr);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Json Debug 日志（自动添加 level 和 time 字段）
    /// </summary>
    public static void LogDomainJsonDebug(this HttpContext context, Dictionary<string, object> data)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        data["level"] = "DEBUG";
        data["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var jsonStr = JsonSerializer.Serialize(data, _jsonOptions);
        logger.Debug(jsonStr);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Json Warn 日志（自动添加 level 和 time 字段）
    /// </summary>
    public static void LogDomainJsonWarn(this HttpContext context, Dictionary<string, object> data)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        data["level"] = "WARN";
        data["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var jsonStr = JsonSerializer.Serialize(data, _jsonOptions);
        logger.Warn(jsonStr);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Json Error 日志（自动添加 level 和 time 字段）
    /// </summary>
    public static void LogDomainJsonError(this HttpContext context, Dictionary<string, object> data)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        data["level"] = "ERROR";
        data["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var jsonStr = JsonSerializer.Serialize(data, _jsonOptions);
        logger.Error(jsonStr);
    }

    /// <summary>
    /// 使用域名 Logger 记录 Json Error 日志（带异常信息，自动添加 level、error、stack 和 time 字段）
    /// </summary>
    public static void LogDomainJsonError(this HttpContext context, Dictionary<string, object> data, Exception ex)
    {
        var logger = context.GetDomainLogger();
        if (logger == null) return;

        data["level"] = "ERROR";
        data["error"] = ex.Message;
        data["stack"] = ex.StackTrace ?? "";
        data["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var jsonStr = JsonSerializer.Serialize(data, _jsonOptions);
        logger.Error(jsonStr);
    }
}
