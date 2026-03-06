using System.ComponentModel;
using System.Text;
using LyWaf.Plugins.Core;
using LyWaf.Shared;
using LyWaf.Utils;
using Microsoft.AspNetCore.Builder;

namespace LyWaf.Plugins.Examples;

/// <summary>
/// 请求日志记录器插件
/// 支持域名/URL 过滤、请求参数记录、响应体记录、独立日志文件输出
/// </summary>
public class RequestLoggerPlugin : LyWafPluginBase
{
    private readonly PluginMetadata _metadata = new()
    {
        Id = "request-logger",
        Name = "请求日志记录器",
        Version = "1.0.0",
        Description = "记录 HTTP 请求详细信息，支持域名/URL 过滤、请求体/响应体记录、独立日志文件",
        Author = "LyWaf Team",
        Priority = PluginPriority.High,
        EnabledByDefault = true,
        DefaultOptions = new RequestLoggerOptions()
    };

    public override PluginMetadata Metadata => _metadata;

    private RequestLoggerOptions Options => (RequestLoggerOptions)_metadata.DefaultOptions!;

    private IDisposable? _eventSubscriptionComplete;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>无规则时的默认日志配置</summary>
    private static readonly MatchRule DefaultRule = new() { IgnorePaths = "/health,/favicon.ico" };

    public override Task InitializeAsync(IPluginContext context)
    {
        base.InitializeAsync(context);

        // 订阅请求完成事件
        _eventSubscriptionComplete = context.SubscribeEvent<RequestCompletedEvent>(async e =>
        {
            if (!Options.Enabled) return;

            var ctx = e.Context;

            // 从中间件获取匹配的规则，或重新查找
            var rule = ctx.Items.TryGetValue("RequestLogger.MatchedRule", out var r) && r is MatchRule mr
                ? mr
                : FindMatchingRule(ctx);
            if (rule == null) return;

            // 构建日志条目
            var logEntry = await BuildLogEntry(ctx, e.StatusCode, e.Duration, rule, Options.MaxBodySize);

            // 写入独立日志文件
            if (Options.LogToFile)
            {
                await WriteToFileAsync(logEntry, rule);
            }

            // 通过 NLog 记录
            if (Options.LogToEvent)
            {
                context.Logger.Info("{LogEntry}", logEntry.TrimEnd());
            }
        });

        context.Logger.Info($"请求日志记录器已初始化 (v{Metadata.Version})");
        return Task.CompletedTask;
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        proxyApp.Use(async (context, next) =>
        {
            if (!Options.Enabled)
            {
                await next(context);
                return;
            }

            var rule = FindMatchingRule(context);
            if (rule == null)
            {
                await next(context);
                return;
            }

            // 存储匹配的规则供事件处理器使用
            context.Items["RequestLogger.MatchedRule"] = rule;

            // 捕获请求体
            if (rule.LogRequestBody)
            {
                await HttpUtil.CaptureRequestBodyAsync(context, Options.MaxBodySize);
            }

            // 捕获响应体（内部调用 next）
            if (rule.LogResponseBody)
            {
                await HttpUtil.CaptureResponseBodyAsync(context, () => next(context), Options.MaxBodySize);
            }
            else
            {
                await next(context);
            }
        });
    }

    /// <summary>
    /// 查找匹配的规则，返回 null 表示不记录
    /// 逻辑：MatchRules 为空则使用默认规则 → 任一规则匹配且路径不在该规则的 IgnorePaths 中则返回该规则
    /// </summary>
    private MatchRule? FindMatchingRule(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // 无规则 = 使用默认规则记录全部（默认规则含 /health,/favicon.ico 忽略）
        if (Options.MatchRules.Count == 0)
        {
            return IsPathIgnored(path, DefaultRule.IgnorePaths) ? null : DefaultRule;
        }

        // 返回第一个匹配的规则（跳过路径被忽略的）
        foreach (var rule in Options.MatchRules)
        {
            if (RuleMatches(context, rule) && !IsPathIgnored(path, rule.IgnorePaths))
                return rule;
        }
        return null;
    }

    /// <summary>
    /// 检查路径是否在逗号分隔的忽略列表中（前缀匹配）
    /// </summary>
    private static bool IsPathIgnored(string path, string ignorePaths)
    {
        if (string.IsNullOrWhiteSpace(ignorePaths)) return false;
        foreach (var segment in ignorePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (path.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 检查单条规则是否匹配：所有非空字段必须全部满足
    /// </summary>
    private static bool RuleMatches(HttpContext context, MatchRule rule)
    {
        var host = context.Request.Host.Host;
        var path = context.Request.Path.Value ?? "/";

        // 全空匹配条件不匹配任何请求
        if (string.IsNullOrWhiteSpace(rule.Host) && string.IsNullOrWhiteSpace(rule.Path) &&
            string.IsNullOrWhiteSpace(rule.Cookie) && string.IsNullOrWhiteSpace(rule.Header) &&
            string.IsNullOrWhiteSpace(rule.QueryParam))
            return false;

        // Host 匹配（支持 *.example.com 通配）
        if (!string.IsNullOrWhiteSpace(rule.Host))
        {
            var pattern = rule.Host.Trim();
            if(pattern != "*")
            {
                if (pattern.StartsWith("*."))
                {
                    var suffix = pattern[1..]; // .example.com
                    if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        !host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (!host.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        // Path 前缀匹配
        if (!string.IsNullOrWhiteSpace(rule.Path))
        {
            if (!path.StartsWith(rule.Path.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Cookie 匹配（格式: name 或 name=value）
        if (!string.IsNullOrWhiteSpace(rule.Cookie))
        {
            if (!MatchKeyValue(rule.Cookie.Trim(),
                name => context.Request.Cookies.ContainsKey(name),
                (name, val) => context.Request.Cookies.TryGetValue(name, out var v) &&
                               v != null && v.Equals(val, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Header 匹配（格式: name 或 name=value）
        if (!string.IsNullOrWhiteSpace(rule.Header))
        {
            if (!MatchKeyValue(rule.Header.Trim(),
                name => context.Request.Headers.ContainsKey(name),
                (name, val) => context.Request.Headers.TryGetValue(name, out var v) &&
                               v.ToString().Contains(val, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // QueryParam 匹配（格式: name 或 name=value）
        if (!string.IsNullOrWhiteSpace(rule.QueryParam))
        {
            if (!MatchKeyValue(rule.QueryParam.Trim(),
                name => context.Request.Query.ContainsKey(name),
                (name, val) => context.Request.Query.TryGetValue(name, out var v) &&
                               v.ToString().Equals(val, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 匹配 "name" (存在检查) 或 "name=value" (值匹配)
    /// </summary>
    private static bool MatchKeyValue(string pattern, Func<string, bool> existsCheck, Func<string, string, bool> valueCheck)
    {
        var eqIdx = pattern.IndexOf('=');
        if (eqIdx > 0)
            return valueCheck(pattern[..eqIdx], pattern[(eqIdx + 1)..]);
        return existsCheck(pattern);
    }

    /// <summary>
    /// 构建格式化的日志条目
    /// </summary>
    private static async Task<string> BuildLogEntry(HttpContext context, int statusCode, TimeSpan duration, MatchRule rule, int maxBodySize)
    {
        var request = context.Request;
        var sb = new StringBuilder();

        sb.AppendLine($"========== [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ==========");
        sb.Append($"{request.Method} {request.Path}");
        if (rule.LogQueryString && request.QueryString.HasValue)
            sb.Append(request.QueryString.Value);
        sb.AppendLine($" {request.Protocol}");
        sb.AppendLine($"Host: {request.Host}");
        sb.AppendLine($"Scheme: {request.Scheme}");

        var clientIp = RequestUtil.GetClientIp(request);
        sb.AppendLine($"Client-IP: {clientIp}");

        if (rule.LogDuration)
            sb.AppendLine($"Status: {statusCode} | Duration: {duration.TotalMilliseconds:F1}ms");
        else
            sb.AppendLine($"Status: {statusCode}");

        // 请求头
        if (rule.LogHeaders)
        {
            sb.AppendLine("--- Headers ---");
            foreach (var header in request.Headers)
            {
                sb.AppendLine($"{header.Key}: {header.Value}");
            }
        }

        // 请求体：通过 HttpUtil 统一捕获并缓存
        if (rule.LogRequestBody)
        {
            var bodyContent = await HttpUtil.CaptureRequestBodyAsync(context, maxBodySize);
            if (!string.IsNullOrEmpty(bodyContent))
            {
                sb.AppendLine("--- Request Body ---");
                sb.AppendLine(bodyContent);
            }
        }

        // 响应体
        if (rule.LogResponseBody)
        {
            var responseBody = await HttpUtil.CaptureResponseBodyAsync(context, maxBytes: maxBodySize);
            if (!string.IsNullOrEmpty(responseBody))
            {
                sb.AppendLine("--- Response Body ---");
                sb.AppendLine(responseBody);
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// 写入独立日志文件
    /// </summary>
    private async Task WriteToFileAsync(string logEntry, MatchRule rule)
    {
        try
        {
            var dir = Path.IsPathRooted(Options.LogDirectory)
                ? Options.LogDirectory
                : Path.Combine(AppContext.BaseDirectory, Options.LogDirectory);
            Directory.CreateDirectory(dir);
            var host = rule.Host.Replace("*", "any");
            var filePath = Path.Combine(dir, $"req_{host}_{DateTime.Now:yyyy-MM-dd}.log");
            await _fileLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(filePath, logEntry, Encoding.UTF8);
            }
            finally
            {
                _fileLock.Release();
            }
        }
        catch (Exception ex)
        {
            Context?.Logger.Error(ex, "写入请求日志文件失败");
        }
    }

    #region 插件操作 — 日志查看

    private static readonly PluginAction ViewLogsAction = new()
    {
        Id = "view-logs",
        Name = "查看日志",
        Type = "log-viewer",
        Description = "查看请求日志文件，支持分页和搜索",
    };

    public override IReadOnlyList<PluginAction> GetActions() => [ViewLogsAction];

    public override async Task<PluginActionResult> ExecuteActionAsync(string actionId, Dictionary<string, string>? parameters)
    {
        if (actionId != "view-logs")
            return new PluginActionResult { Success = false, Message = $"未知操作: {actionId}" };

        var dir = GetLogDirectory();
        parameters ??= [];

        // 子操作: list-files / read-entries / delete-file
        var subAction = parameters.GetValueOrDefault("action", "list-files");

        return subAction switch
        {
            "list-files" => new PluginActionResult
            {
                Data = new { type = "file-list", files = LogUtil.ListLogFiles(dir, "req_*.log") }
            },
            "read-entries" => await ReadLogEntriesAsync(dir, parameters),
            "delete-file" => DeleteLogFileAction(dir, parameters),
            "clear-file" => await ClearLogFileAction(dir, parameters),
            "delete-entry" => await DeleteLogEntryAction(dir, parameters),
            _ => new PluginActionResult { Success = false, Message = $"未知子操作: {subAction}" },
        };
    }

    private async Task<PluginActionResult> ReadLogEntriesAsync(string dir, Dictionary<string, string> parameters)
    {
        var fileName = parameters.GetValueOrDefault("file", "");
        if (string.IsNullOrEmpty(fileName))
            return new PluginActionResult { Success = false, Message = "请指定日志文件名" };

        // 安全检查：防止路径穿越
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return new PluginActionResult { Success = false, Message = "无效的文件名" };

        var filePath = Path.Combine(dir, fileName);
        if (!File.Exists(filePath))
            return new PluginActionResult { Success = false, Message = $"文件不存在: {fileName}" };

        _ = int.TryParse(parameters.GetValueOrDefault("offset", "0"), out var offset);
        _ = int.TryParse(parameters.GetValueOrDefault("limit", "20"), out var limit);
        var search = parameters.TryGetValue("search", out var s) ? s : null;

        var (entries, total) = await LogUtil.ParseLogFileAsync(filePath, offset, Math.Clamp(limit, 1, 100), search);
        var fileInfo = new FileInfo(filePath);

        return new PluginActionResult
        {
            Data = new
            {
                type = "entries",
                entries,
                total,
                offset,
                limit,
                fileName,
                fileSize = fileInfo.Length,
            }
        };
    }

    private static PluginActionResult DeleteLogFileAction(string dir, Dictionary<string, string> parameters)
    {
        var fileName = parameters.GetValueOrDefault("file", "");
        if (string.IsNullOrEmpty(fileName))
            return new PluginActionResult { Success = false, Message = "请指定日志文件名" };

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return new PluginActionResult { Success = false, Message = "无效的文件名" };

        var filePath = Path.Combine(dir, fileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            return new PluginActionResult { Message = $"已删除: {fileName}" };
        }
        return new PluginActionResult { Success = false, Message = $"文件不存在: {fileName}" };
    }

    private static async Task<PluginActionResult> ClearLogFileAction(string dir, Dictionary<string, string> parameters)
    {
        var fileName = parameters.GetValueOrDefault("file", "");
        if (string.IsNullOrEmpty(fileName))
            return new PluginActionResult { Success = false, Message = "请指定日志文件名" };

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return new PluginActionResult { Success = false, Message = "无效的文件名" };

        var filePath = Path.Combine(dir, fileName);
        if (!File.Exists(filePath))
            return new PluginActionResult { Success = false, Message = $"文件不存在: {fileName}" };

        await LogUtil.ClearLogFileAsync(filePath);
        return new PluginActionResult { Message = $"已清空: {fileName}" };
    }

    private static async Task<PluginActionResult> DeleteLogEntryAction(string dir, Dictionary<string, string> parameters)
    {
        var fileName = parameters.GetValueOrDefault("file", "");
        if (string.IsNullOrEmpty(fileName))
            return new PluginActionResult { Success = false, Message = "请指定日志文件名" };

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return new PluginActionResult { Success = false, Message = "无效的文件名" };

        if (!int.TryParse(parameters.GetValueOrDefault("index", ""), out var entryIndex))
            return new PluginActionResult { Success = false, Message = "请指定条目索引" };

        var filePath = Path.Combine(dir, fileName);
        if (!File.Exists(filePath))
            return new PluginActionResult { Success = false, Message = $"文件不存在: {fileName}" };

        var deleted = await LogUtil.DeleteLogEntryAsync(filePath, entryIndex);
        return deleted
            ? new PluginActionResult { Message = $"已删除条目 #{entryIndex + 1}" }
            : new PluginActionResult { Success = false, Message = $"条目索引无效: {entryIndex}" };
    }

    private string GetLogDirectory()
    {
        return Path.IsPathRooted(Options.LogDirectory)
            ? Options.LogDirectory
            : Path.Combine(AppContext.BaseDirectory, Options.LogDirectory);
    }

    #endregion

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSubscriptionComplete?.Dispose();
        Context?.Logger.Info("请求日志记录器已停止");
        return base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// 请求日志记录器配置
/// </summary>
public class RequestLoggerOptions
{
    /// <summary>是否启用</summary>
    [Description("启用")]
    public bool Enabled { get; set; } = true;

    /// <summary>匹配规则（空=记录全部，任一规则匹配即记录）</summary>
    [Description("匹配规则")]
    public List<MatchRule> MatchRules { get; set; } = [];

    /// <summary>是否通过 NLog 记录</summary>
    [Description("通过 NLog 记录")]
    public bool LogToEvent { get; set; } = false;

    /// <summary>是否写入独立日志文件</summary>
    [Description("写入独立日志文件")]
    public bool LogToFile { get; set; } = true;

    /// <summary>日志文件目录</summary>
    [Description("日志文件目录")]
    public string LogDirectory { get; set; } = "logs/request_logger";

    /// <summary>最大请求体/响应体大小（字节）</summary>
    [Description("最大Body大小(字节)")]
    public int MaxBodySize { get; set; } = 64 * 1024;
}

/// <summary>
/// 匹配规则：同一规则内的非空匹配条件全部满足(AND)，多条规则之间任一满足(OR)
/// 每条规则同时定义匹配条件和该规则的日志记录选项
/// </summary>
public class MatchRule
{
    /// <summary>域名（支持 *.example.com 通配）</summary>
    [Description("域名")]
    public string Host { get; set; } = "";

    /// <summary>路径前缀（如 /api）</summary>
    [Description("路径")]
    public string Path { get; set; } = "";

    /// <summary>Cookie（格式: name 或 name=value）</summary>
    [Description("Cookie")]
    public string Cookie { get; set; } = "";

    /// <summary>请求头（格式: name 或 name=value）</summary>
    [Description("请求头匹配")]
    public string Header { get; set; } = "";

    /// <summary>查询参数（格式: name 或 name=value）</summary>
    [Description("查询参数匹配")]
    public string QueryParam { get; set; } = "";

    /// <summary>忽略的路径前缀（逗号分隔，如 /health,/favicon.ico）</summary>
    [Description("忽略路径")]
    public string IgnorePaths { get; set; } = "";

    /// <summary>是否记录请求头</summary>
    [Description("记录请求头")]
    public bool LogHeaders { get; set; } = false;

    /// <summary>是否记录查询字符串</summary>
    [Description("记录查询字符串")]
    public bool LogQueryString { get; set; } = true;

    /// <summary>是否记录请求体</summary>
    [Description("记录请求体")]
    public bool LogRequestBody { get; set; } = false;

    /// <summary>是否记录响应体</summary>
    [Description("记录响应体")]
    public bool LogResponseBody { get; set; } = false;

    /// <summary>是否记录响应时间</summary>
    [Description("记录响应时间")]
    public bool LogDuration { get; set; } = true;
}
