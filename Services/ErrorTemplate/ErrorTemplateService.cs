using System.Collections.Concurrent;
using LyWaf.Shared;
using LyWaf.Utils;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.ErrorTemplate;

/// <summary>
/// 错误模板服务接口
/// </summary>
public interface IErrorTemplateService
{
    /// <summary>
    /// 获取指定状态码的模板内容（已替换占位符）
    /// </summary>
    /// <param name="statusCode">HTTP 状态码</param>
    /// <param name="context">HTTP 上下文</param>
    /// <param name="extraValues">额外的占位符值（如 reason、custom 等）</param>
    Task<string> GetTemplateAsync(int statusCode, HttpContext context, Dictionary<string, string?>? extraValues = null);

    /// <summary>
    /// 获取指定状态码的模板配置
    /// </summary>
    ErrorTemplateConfig GetConfig(int statusCode);

    /// <summary>
    /// 写入错误响应（统一入口）
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <param name="statusCode">HTTP 状态码</param>
    /// <param name="extraValues">额外的占位符值（支持 reason、custom 等任意参数）</param>
    Task WriteErrorResponseAsync(HttpContext context, int statusCode, Dictionary<string, string?>? extraValues = null);

    /// <summary>
    /// 写入 403 Forbidden 响应
    /// </summary>
    Task WriteForbiddenAsync(HttpContext context, Dictionary<string, string?>? extraValues = null);

    /// <summary>
    /// 写入 429 Too Many Requests 响应
    /// </summary>
    Task WriteTooManyRequestsAsync(HttpContext context, Dictionary<string, string?>? extraValues = null);

    /// <summary>
    /// 写入 502 Bad Gateway 响应
    /// </summary>
    Task WriteBadGatewayAsync(HttpContext context, Dictionary<string, string?>? extraValues = null);

    /// <summary>
    /// 写入 503 Service Unavailable 响应
    /// </summary>
    Task WriteServiceUnavailableAsync(HttpContext context, Dictionary<string, string?>? extraValues = null);
}

/// <summary>
/// 错误模板服务实现
/// </summary>
public class ErrorTemplateService : IErrorTemplateService
{
    private readonly ErrorTemplateOptions _options;
    private readonly string _projectRoot;
    private readonly ConcurrentDictionary<string, string> _templateCache = new();

    public ErrorTemplateService(IOptions<ErrorTemplateOptions> options, string? projectRoot = null)
    {
        _options = options.Value;
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
    }

    public ErrorTemplateConfig GetConfig(int statusCode)
    {
        return statusCode switch
        {
            403 => _options.Forbidden,
            404 => _options.NotFound,
            429 => _options.TooManyRequests,
            500 => _options.InternalError,
            502 => _options.BadGateway,
            503 => _options.ServiceUnavailable,
            _ => _options.Custom.TryGetValue(statusCode.ToString(), out var custom) ? custom : new ErrorTemplateConfig()
        };
    }

    public async Task<string> GetTemplateAsync(int statusCode, HttpContext context, Dictionary<string, string?>? extraValues = null)
    {
        var config = GetConfig(statusCode);
        var template = await GetRawTemplateAsync(statusCode, config);
        return ReplaceVariables(template, context, statusCode, extraValues);
    }

    private async Task<string> GetRawTemplateAsync(int statusCode, ErrorTemplateConfig config)
    {
        // 优先检查 .lywaf.{statusCode}.html 编辑版本（无条件覆盖任何配置）
        var editPath = GetEditFilePath(statusCode);
        var editCacheKey = $"edit:{statusCode}";
        if (File.Exists(editPath))
        {
            if (_templateCache.TryGetValue(editCacheKey, out var editCached))
                return editCached;
            var editContent = await File.ReadAllTextAsync(editPath);
            _templateCache[editCacheKey] = editContent;
            return editContent;
        }

        switch (config.Type)
        {
            case TemplateType.File when !string.IsNullOrEmpty(config.FilePath):
                var cacheKey = $"file:{config.FilePath}";
                if (_templateCache.TryGetValue(cacheKey, out var cached))
                    return cached;

                var filePath = Path.IsPathRooted(config.FilePath)
                    ? config.FilePath
                    : Path.Combine(_projectRoot, config.FilePath);

                if (File.Exists(filePath))
                {
                    var content = await File.ReadAllTextAsync(filePath);
                    _templateCache[cacheKey] = content;
                    return content;
                }
                // 文件不存在，使用默认模板
                return GetDefaultTemplate(statusCode);

            case TemplateType.Inline when !string.IsNullOrEmpty(config.Content):
                return config.Content;

            default:
                return GetDefaultTemplate(statusCode);
        }
    }

    /// <summary>
    /// 获取编辑版模板文件路径：templates/.lywaf.{statusCode}.html
    /// </summary>
    public string GetEditFilePath(int statusCode)
    {
        return Path.Combine(_projectRoot, "templates", $".lywaf.{statusCode}.html");
    }

    /// <summary>
    /// 获取原始模板文件路径：templates/{statusCode}.html
    /// </summary>
    public string GetOriginalFilePath(int statusCode)
    {
        return Path.Combine(_projectRoot, "templates", $"{statusCode}.html");
    }

    private string ReplaceVariables(string template, HttpContext context, int statusCode, Dictionary<string, string?>? inputValues)
    {
        var statusText = GetStatusText(statusCode);
        
        // 构建基础占位符值
        var values = new Dictionary<string, string?>
        {
            // 状态码相关
            ["status_code"] = statusCode.ToString(),
            ["StatusCode"] = statusCode.ToString(),
            ["status_text"] = statusText,
            ["StatusText"] = statusText,
            // 兼容旧格式
            ["local_client_ip"] = RequestUtil.GetClientIp(context.Request)
        };

        // 合并传入的额外值
        if (inputValues != null)
        {
            foreach (var kv in inputValues)
            {
                values[kv.Key] = kv.Value;
            }
        }

        // 获取 reason 值（支持多种 key）
        var reason = values.GetValueOrDefault("reason") 
                  ?? values.GetValueOrDefault("Reason");

        // 处理原因信息显示
        if (_options.ShowReason && !string.IsNullOrEmpty(reason))
        {
            values["reason"] = reason;
            values["Reason"] = reason;
            values["show_reason"] = reason;
            values["show_reason_info"] = $"<h3 align=\"center\">原因: {reason}</h3>";
        }
        else
        {
            values.TryAdd("reason", "");
            values.TryAdd("Reason", "");
            values.TryAdd("show_reason", "");
            values.TryAdd("show_reason_info", "");
        }

        // 使用 StringUtil 高效替换
        return StringUtil.FormatTemplate(template, context, values);
    }

    private static string GetStatusText(int statusCode)
    {
        return statusCode switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            408 => "Request Timeout",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => "Error"
        };
    }

    private static string GetDefaultTemplate(int statusCode)
    {
        var statusText = GetStatusText(statusCode);
        var description = statusCode switch
        {
            403 => "您的IP存在异常访问的情况，若误封请联系管理员。",
            404 => "您请求的页面不存在。",
            429 => "请求过于频繁，请稍后再试。",
            500 => "服务器内部错误，请稍后再试。",
            502 => "网关错误，后端服务暂时不可用。",
            503 => "服务暂时不可用，请稍后再试。",
            _ => "发生错误，请稍后再试。"
        };

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{statusCode}} {{statusText}} - LyWaf</title>
                <style>
                    * { margin: 0; padding: 0; box-sizing: border-box; }
                    body {
                        font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                        background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
                        min-height: 100vh;
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        color: #e4e4e7;
                    }
                    .container {
                        text-align: center;
                        padding: 2rem;
                        width: 100%;
                        max-width: 600px;
                    }
                    .status-code {
                        font-size: 8rem;
                        font-weight: 700;
                        background: linear-gradient(135deg, #f43f5e, #ec4899);
                        -webkit-background-clip: text;
                        -webkit-text-fill-color: transparent;
                        background-clip: text;
                        line-height: 1;
                        margin-bottom: 1rem;
                    }
                    .status-text {
                        font-size: 1.5rem;
                        color: #a1a1aa;
                        margin-bottom: 1.5rem;
                    }
                    .description {
                        font-size: 1.1rem;
                        color: #71717a;
                        margin-bottom: 2rem;
                        line-height: 1.6;
                    }
                    .info {
                        background: rgba(255,255,255,0.05);
                        border-radius: 12px;
                        padding: 1.5rem;
                        margin-top: 2rem;
                    }
                    .info-item {
                        display: flex;
                        justify-content: space-between;
                        padding: 0.5rem 0;
                        border-bottom: 1px solid rgba(255,255,255,0.1);
                    }
                    .info-item:last-child { border-bottom: none; }
                    .info-label { color: #a1a1aa; }
                    .info-value { color: #e4e4e7; font-family: monospace; }
                    .reason {
                        margin-top: 1.5rem;
                        padding: 1rem;
                        background: rgba(244, 63, 94, 0.1);
                        border: 1px solid rgba(244, 63, 94, 0.3);
                        border-radius: 8px;
                        color: #fda4af;
                    }
                    .footer {
                        margin-top: 3rem;
                        color: #52525b;
                        font-size: 0.875rem;
                    }
                </style>
            </head>
            <body>
                <div class="container">
                    <div class="status-code">{{statusCode}}</div>
                    <div class="status-text">{{statusText}}</div>
                    <div class="description">{{description}}</div>
                    <div class="info">
                        <div class="info-item">
                            <span class="info-label">您的IP</span>
                            <span class="info-value">{client_ip}</span>
                        </div>
                        <div class="info-item">
                            <span class="info-label">请求路径</span>
                            <span class="info-value">{path}</span>
                        </div>
                        <div class="info-item">
                            <span class="info-label">时间</span>
                            <span class="info-value">{time}</span>
                        </div>
                    </div>
                    {show_reason_info}
                    <div class="footer">Powered by LyWaf</div>
                </div>
            </body>
            </html>
            """;
    }

    public async Task WriteErrorResponseAsync(HttpContext context, int statusCode, Dictionary<string, string?>? extraValues = null)
    {
        if (context.Response.HasStarted)
            return;

        var config = GetConfig(statusCode);
        var template = await GetTemplateAsync(statusCode, context, extraValues);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = config.ContentType;

        // 添加额外的响应头
        foreach (var header in config.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        await context.Response.WriteAsync(template);
    }

    public Task WriteForbiddenAsync(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorResponseAsync(context, 403, extraValues);

    public Task WriteTooManyRequestsAsync(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorResponseAsync(context, 429, extraValues);

    public Task WriteBadGatewayAsync(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorResponseAsync(context, 502, extraValues);

    public Task WriteServiceUnavailableAsync(HttpContext context, Dictionary<string, string?>? extraValues = null)
        => WriteErrorResponseAsync(context, 503, extraValues);

    /// <summary>
    /// 清除模板缓存
    /// </summary>
    public void ClearCache()
    {
        _templateCache.Clear();
    }

    /// <summary>
    /// 重新加载指定文件模板
    /// </summary>
    public void ReloadTemplate(string filePath)
    {
        var cacheKey = $"file:{filePath}";
        _templateCache.TryRemove(cacheKey, out _);
    }
}
