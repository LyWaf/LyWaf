using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;

namespace LyWaf.Plugins.Examples;

public partial class RequestLoggerPlugin : LyWafPluginBase
{
    private readonly PluginMetadata metadata = new()
    {
        Id = "request-logger", LocalizationResourcePrefix = "LyWaf.Plugins.Examples.RequestLogger.Localization",
        Name = "请求日志记录器", Version = "1.1.0", Author = "LyWaf Team", Priority = PluginPriority.High,
        Description = "记录 HTTP 请求，支持脱敏、过滤、异步写入、轮转及结构化日志",
        EnabledByDefault = true, DefaultOptions = new RequestLoggerOptions()
    };
    public override PluginMetadata Metadata => metadata;
    private RequestLoggerOptions Options => (RequestLoggerOptions)metadata.DefaultOptions!;
    private IDisposable? subscription;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private RequestLogger.RequestLogWriter? _writer;
    private static readonly MatchRule DefaultRule = new() { IgnorePaths = "/health,/favicon.ico" };

    public override async Task InitializeAsync(IPluginContext context)
    {
        await base.InitializeAsync(context);
        subscription?.Dispose();
        subscription = context.SubscribeEvent<RequestCompletedEvent>(ProcessCompletedAsync);
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var rule = Options.Enabled ? FindMatchingRule(context) : null;
        if (rule is null) { await next(context); return; }
        context.Items["RequestLogger.MatchedRule"] = rule;
        await CaptureAsync(context, () => next(context), rule);
    });

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose(); subscription = null;
        if (_writer is { } writer) await writer.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private MatchRule? FindMatchingRule(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        return Options.MatchRules.Count == 0 ? IsPathIgnored(path, DefaultRule.IgnorePaths) ? null : DefaultRule
            : Options.MatchRules.FirstOrDefault(rule => RuleMatches(context, rule) && !IsPathIgnored(path, rule.IgnorePaths));
    }

    private static bool IsPathIgnored(string path, string? ignored) => !string.IsNullOrWhiteSpace(ignored)
        && ignored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool RuleMatches(HttpContext context, MatchRule rule)
    {
        var request = context.Request;
        if (new[] { rule.Host, rule.Path, rule.Cookie, rule.Header, rule.QueryParam }.All(string.IsNullOrWhiteSpace)) return false;
        if (!string.IsNullOrWhiteSpace(rule.Host))
        {
            var pattern = rule.Host.Trim();
            if (pattern != "*" && !(pattern.StartsWith("*.", StringComparison.Ordinal)
                ? request.Host.Host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                    || request.Host.Host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase)
                : request.Host.Host.Equals(pattern, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (!string.IsNullOrWhiteSpace(rule.Path) && !(request.Path.Value ?? "/").StartsWith(rule.Path.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return Match(rule.Cookie, name => request.Cookies.ContainsKey(name), (name, value) => request.Cookies.TryGetValue(name, out var actual)
                && actual.Equals(value, StringComparison.OrdinalIgnoreCase))
            && Match(rule.Header, name => request.Headers.ContainsKey(name), (name, value) => request.Headers.TryGetValue(name, out var actual)
                && actual.ToString().Contains(value, StringComparison.OrdinalIgnoreCase))
            && Match(rule.QueryParam, name => request.Query.ContainsKey(name), (name, value) => request.Query.TryGetValue(name, out var actual)
                && actual.ToString().Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Match(string? pattern, Func<string, bool> exists, Func<string, string, bool> matches)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        pattern = pattern.Trim();
        var separator = pattern.IndexOf('=');
        return separator > 0 ? matches(pattern[..separator], pattern[(separator + 1)..]) : exists(pattern);
    }
}

public class RequestLoggerOptions
{
    [PluginConfiguration(LabelKey = "config.Enabled.label")]
    [Description("启用")] public bool Enabled { get; set; } = true;
    [PluginConfiguration(LabelKey = "config.MatchRules.label")]
    [Description("匹配规则")] public List<MatchRule> MatchRules { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.LogToEvent.label")]
    [Description("通过 NLog 记录")] public bool LogToEvent { get; set; }
    [PluginConfiguration(LabelKey = "config.LogToFile.label")]
    [Description("写入独立日志文件")] public bool LogToFile { get; set; } = true;
    [PluginConfiguration(LabelKey = "config.LogDirectory.label")]
    [Description("日志文件目录")] public string LogDirectory { get; set; } = "logs/request_logger";
    [PluginConfiguration(LabelKey = "config.MaxBodySize.label")]
    [Description("最大Body大小(字节)"), Range(256, 1048576)] public int MaxBodySize { get; set; } = 64 * 1024;
    [PluginConfiguration(LabelKey = "config.Format.label", DescriptionKey = "config.Format.description"), RegularExpression("(?i)^(text|json)$")]
    public string Format { get; set; } = "text";
    [PluginConfiguration(LabelKey = "config.QueueCapacity.label", RequiresRestart = true), Range(16, 4096)]
    public int QueueCapacity { get; set; } = 256;
    [PluginConfiguration(LabelKey = "config.MaxQueuedBytes.label", RequiresRestart = true), Range(1048576, 67108864)]
    public long MaxQueuedBytes { get; set; } = 16 * 1024 * 1024;
    [PluginConfiguration(LabelKey = "config.BatchSize.label"), Range(1, 256)] public int BatchSize { get; set; } = 64;
    [PluginConfiguration(LabelKey = "config.FlushIntervalMs.label"), Range(10, 10000)] public int FlushIntervalMs { get; set; } = 1000;
    [PluginConfiguration(LabelKey = "config.MaxFileBytes.label"), Range(4096, 1073741824)] public long MaxFileBytes { get; set; } = 64 * 1024 * 1024;
    [PluginConfiguration(LabelKey = "config.MaxFiles.label"), Range(1, 1000)] public int MaxFiles { get; set; } = 30;
    [PluginConfiguration(LabelKey = "config.RetentionDays.label"), Range(1, 3650)] public int RetentionDays { get; set; } = 14;
    [PluginConfiguration(LabelKey = "config.SensitiveFields.label", DescriptionKey = "config.SensitiveFields.description")]
    public List<string> SensitiveFields { get; set; } = [];
}

public class MatchRule
{
    [PluginConfiguration(LabelKey = "config.Host.label")]
    [Description("域名")] public string Host { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.Path.label")]
    [Description("路径")] public string Path { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.Cookie.label")]
    [Description("Cookie")] public string Cookie { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.Header.label")]
    [Description("请求头匹配")] public string Header { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.QueryParam.label")]
    [Description("查询参数匹配")] public string QueryParam { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.IgnorePaths.label")]
    [Description("忽略路径")] public string IgnorePaths { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.LogHeaders.label")]
    [Description("记录请求头")] public bool LogHeaders { get; set; }
    [PluginConfiguration(LabelKey = "config.LogQueryString.label")]
    [Description("记录查询字符串")] public bool LogQueryString { get; set; } = true;
    [PluginConfiguration(LabelKey = "config.LogRequestBody.label")]
    [Description("记录请求体")] public bool LogRequestBody { get; set; }
    [PluginConfiguration(LabelKey = "config.LogResponseBody.label")]
    [Description("记录响应体")] public bool LogResponseBody { get; set; }
    [PluginConfiguration(LabelKey = "config.LogDuration.label")]
    [Description("记录响应时间")] public bool LogDuration { get; set; } = true;
    [PluginConfiguration(LabelKey = "config.FilePrefix.label")]
    [Description("文件名前缀")] public string FilePrefix { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.Methods.label")] public string Methods { get; set; } = "";
    [PluginConfiguration(LabelKey = "config.StatusCodes.label", DescriptionKey = "config.StatusCodes.description")]
    public List<string> StatusCodes { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.MinDurationMs.label"), Range(0, 86400000)] public double MinDurationMs { get; set; }
    [PluginConfiguration(LabelKey = "config.SampleRate.label"), Range(0, 1)] public double SampleRate { get; set; } = 1;
}
