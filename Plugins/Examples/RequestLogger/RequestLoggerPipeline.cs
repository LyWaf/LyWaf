using System.Text;
using LyWaf.Plugins.Core;
using LyWaf.Shared;
using LyWaf.Utils;

namespace LyWaf.Plugins.Examples;

public partial class RequestLoggerPlugin
{
    private const string RequestCapture = "RequestLogger.RequestCapture";
    private const string ResponseCapture = "RequestLogger.ResponseCapture";
    private const string FailureKey = "RequestLogger.Failure";
    internal RequestLogger.RequestLogWriterStatus WriterStatus => _writer?.Status ?? new(0, 0, 0, 0, 0);
    private int BodyLimit => Math.Clamp(Options.MaxBodySize, 256, 1024 * 1024);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null && State == PluginState.Running) return Task.CompletedTask;
        _writer = new(Math.Clamp(Options.QueueCapacity, 16, 4096), Math.Clamp(Options.MaxQueuedBytes, 1024 * 1024, 64L * 1024 * 1024),
            _fileLock, IsOwnedLogFile, error => Context?.Logger.Warn("Request log write failed ({FailureType})", error.GetType().Name));
        return base.StartAsync(cancellationToken);
    }

    private async Task CaptureAsync(HttpContext context, Func<Task> next, MatchRule rule)
    {
        var originalRequest = context.Request.Body;
        var originalResponse = context.Response.Body;
        var websocket = context.WebSockets.IsWebSocketRequest
            || context.Request.Headers.Upgrade.ToString().Equals("websocket", StringComparison.OrdinalIgnoreCase);
        using var request = rule.LogRequestBody && !websocket ? new CaptureStream(originalRequest, BodyLimit + 1) : null;
        using var response = rule.LogResponseBody && !websocket ? new CaptureStream(originalResponse, BodyLimit + 1) : null;
        if (request is not null) context.Request.Body = request;
        if (response is not null) context.Response.Body = response;
        try { await next(); }
        catch (Exception error) { context.Items[FailureKey] = error.GetType().Name; throw; }
        finally
        {
            if (request is not null) context.Items[RequestCapture] = request.GetCapturedBody(BodyLimit + 1) ?? "";
            if (response is not null) context.Items[ResponseCapture] = response.GetCapturedBody(BodyLimit + 1) ?? "";
            context.Request.Body = originalRequest;
            context.Response.Body = originalResponse;
        }
    }

    internal Task ProcessCompletedAsync(RequestCompletedEvent completed)
    {
        if (!Options.Enabled || State != PluginState.Running) return Task.CompletedTask;
        try
        {
            var context = completed.Context;
            var status = context.RequestAborted.IsCancellationRequested ? 499
                : context.Items.ContainsKey(FailureKey) && completed.StatusCode < 400 ? 500 : completed.StatusCode;
            if (context.Items.TryGetValue("RequestLogger.InterceptRule", out var intercepted) && intercepted is MatchRule interceptRule)
                Emit(context, completed, status, interceptRule);
            var rule = context.Items.TryGetValue("RequestLogger.MatchedRule", out var matched) && matched is MatchRule captured
                ? captured : FindMatchingRule(context);
            if (rule is not null && ShouldLog(rule, context.Request.Method, status, completed.Duration)) Emit(context, completed, status, rule);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Context?.Logger.Warn("Request log capture failed ({FailureType})", error.GetType().Name); }
        return Task.CompletedTask;
    }

    internal static bool ShouldLog(MatchRule rule, string method, int status, TimeSpan duration)
    {
        if (!string.IsNullOrWhiteSpace(rule.Methods) && !rule.Methods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(method, StringComparer.OrdinalIgnoreCase)) return false;
        if (rule.StatusCodes is { Count: > 0 } && !rule.StatusCodes.Any(pattern => StatusMatches(pattern, status))) return false;
        if (!double.IsFinite(rule.MinDurationMs) || duration.TotalMilliseconds < Math.Max(0, rule.MinDurationMs)) return false;
        return double.IsFinite(rule.SampleRate) && rule.SampleRate > 0 && (rule.SampleRate >= 1 || Random.Shared.NextDouble() < rule.SampleRate);
    }

    internal static bool StatusMatches(string? pattern, int status)
    {
        if (pattern is null || status is < 100 or > 599) return false;
        pattern = pattern.Trim();
        if (pattern == "*") return true;
        var code = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return pattern.Length == 3 && Enumerable.Range(0, 3).All(index => pattern[index] is 'x' or 'X' || pattern[index] == code[index]);
    }

    private void Emit(HttpContext context, RequestCompletedEvent completed, int status, MatchRule rule)
    {
        var redactor = new RequestLogger.RequestLogRedactor(Options.SensitiveFields);
        var limit = BodyLimit;
        var requestBody = rule.LogRequestBody ? Captured(context, RequestCapture, LyWafCache.RequestBody, LyWafCache.RequestCaptureStream, limit + 1) : null;
        var responseBody = rule.LogResponseBody ? Captured(context, ResponseCapture, LyWafCache.ResponseBody, LyWafCache.ResponseCaptureStream, limit + 1) : null;
        var requestTruncated = requestBody is not null && Encoding.UTF8.GetByteCount(requestBody) > limit;
        var responseTruncated = responseBody is not null && Encoding.UTF8.GetByteCount(responseBody) > limit;
        var request = context.Request;
        var record = new RequestLogger.RequestLogRecord(new DateTimeOffset(completed.VisitTime == default ? DateTime.UtcNow : completed.VisitTime.ToUniversalTime()),
            RequestLogger.RequestLogRedactor.Line(context.TraceIdentifier, 256), RequestLogger.RequestLogRedactor.Line(request.Method, 32),
            RequestLogger.RequestLogRedactor.Line(request.PathBase + request.Path, 8192), rule.LogQueryString ? redactor.Query(request.QueryString.Value) : "",
            RequestLogger.RequestLogRedactor.Line(request.Host.Value, 512), RequestLogger.RequestLogRedactor.Line(request.Scheme, 16),
            RequestLogger.RequestLogRedactor.Line(request.Protocol, 32), RequestUtil.GetClientIp(request), status,
            rule.LogDuration ? Math.Round(completed.Duration.TotalMilliseconds, 3) : null, request.ContentLength, context.Response.ContentLength,
            context.Items.TryGetValue("RequestLogger.Reason", out var reason) ? RequestLogger.RequestLogRedactor.Line(reason?.ToString(), 2048) : null,
            context.RequestAborted.IsCancellationRequested, context.Items.TryGetValue(FailureKey, out var failure) ? failure?.ToString() : null,
            rule.LogHeaders ? request.Headers.Take(64).ToDictionary(header => RequestLogger.RequestLogRedactor.Line(header.Key, 256),
                header => redactor.IsSensitive(header.Key) ? "[REDACTED]" : RequestLogger.RequestLogRedactor.Line(header.Value.ToString(), 2048), StringComparer.OrdinalIgnoreCase) : null,
            redactor.Body(requestBody, request.ContentType, limit, requestTruncated),
            redactor.Body(responseBody, context.Response.ContentType, limit, responseTruncated), requestTruncated, responseTruncated);
        var json = string.Equals(Options.Format, "json", StringComparison.OrdinalIgnoreCase);
        var entry = json ? record.ToJsonLine() : record.ToText();
        if (Options.LogToFile)
        {
            var prefix = rule.FilePrefix.Equals("intercept", StringComparison.OrdinalIgnoreCase) ? "intercept"
                : "req_" + SafePrefix(string.IsNullOrWhiteSpace(rule.FilePrefix) ? rule.Host.Replace("*", "any") : rule.FilePrefix);
            var path = Path.Combine(GetLogDirectory(), $"{prefix}_{record.Timestamp.LocalDateTime:yyyy-MM-dd}.{(json ? "jsonl" : "log")}");
            _writer?.TryWrite(path, entry, new(Math.Clamp(Options.MaxFileBytes, 4096, 1024L * 1024 * 1024),
                Math.Clamp(Options.MaxFiles, 1, 1000), Math.Clamp(Options.RetentionDays, 1, 3650),
                Math.Clamp(Options.BatchSize, 1, 256), Math.Clamp(Options.FlushIntervalMs, 10, 10000)));
        }
        if (Options.LogToEvent) Context?.Logger.Info("{LogEntry}", entry.TrimEnd());
    }

    private static string? Captured(HttpContext context, string ownKey, string cacheKey, string streamKey, int limit)
    {
        if (context.Items.TryGetValue(ownKey, out var own) && own is string snapshot) return snapshot;
        if (context.Items.TryGetValue(cacheKey, out var cached) && cached is string text)
            return text.Length > limit ? text[..limit] : text;
        return context.Items.TryGetValue(streamKey, out var stream) && stream is CaptureStream captured ? captured.GetCapturedBody(limit) : null;
    }

    internal static string SafePrefix(string? value)
    {
        var safe = new string((value ?? "").Take(64).Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "all" : safe.Replace("..", "__");
    }

    private bool IsOwnedLogFile(string name)
    {
        if (name.Length > 255 || name != Path.GetFileName(name) || name.Contains(':') || name.Contains("..")
            || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !(name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))) return false;
        return name.StartsWith("req_", StringComparison.Ordinal) || name.StartsWith("intercept_", StringComparison.Ordinal)
            || Options.MatchRules.Where(rule => !string.IsNullOrWhiteSpace(rule.FilePrefix))
                .Any(rule => name.StartsWith(SafePrefix(rule.FilePrefix) + "_", StringComparison.Ordinal));
    }
}
