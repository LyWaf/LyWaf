using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using NLog;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.ManagementApi;

/// <summary>管理 API 失败响应的稳定错误明细。</summary>
public sealed record ApiError(string Code, string Message, string TraceId, object? Details = null);

/// <summary>
/// 管理 API 的统一失败信封。顶层 Message 为旧版控制台保留，新增客户端应读取 Error。
/// </summary>
public sealed record ApiErrorEnvelope(bool Success, string Message, ApiError Error);

/// <summary>管理 API 版本转换、响应约束与 OpenAPI 文档生成入口。</summary>
public static class ManagementApiContract
{
    public const string CurrentVersion = "1";
    public const string VersionPrefix = "/api/v1";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>安装管理 API 契约中间件，仅作用于控制端口。</summary>
    public static WebApplication UseManagementApiContract(this WebApplication app, int controlPort)
    {
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort != controlPort || !context.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var metrics = context.RequestServices.GetRequiredService<PrometheusMetrics>();
            if (metrics.Enabled)
            {
                var observation = metrics.StartAdminRequest();
                context.Response.OnCompleted(() =>
                {
                    observation.Complete(context.Request.Method, context.Response.StatusCode);
                    return Task.CompletedTask;
                });
            }

            var versioned = TryRewriteVersionedPath(context.Request.Path, out var rewritten);
            if (versioned) context.Request.Path = rewritten;

            var settings = context.RequestServices.GetRequiredService<IOptionsMonitor<ManagementApiOptions>>().CurrentValue;
            var write = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
                        HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
            var identity = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            context.Response.Headers["X-LyWaf-Api-Version"] = CurrentVersion;
            context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
            if (!versioned)
            {
                context.Response.Headers["Deprecation"] = "true";
                context.Response.Headers["Sunset"] = "Thu, 03 Sep 2027 00:00:00 GMT";
                context.Response.Headers["Link"] = $"<{VersionPrefix}>; rel=\"successor-version\"";
            }

            try
            {
                var maxBody = Math.Clamp(settings.MaxRequestBodyBytes, 1024, 64L * 1024 * 1024);
                var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maxBody;
                if (context.Request.ContentLength > maxBody)
                {
                    await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "管理 API 请求体超过限制");
                    return;
                }
                var guard = context.RequestServices.GetRequiredService<ManagementApiGuard>();
                if (!guard.TryAcquire(identity, write, DateTimeOffset.UtcNow, out var retryAfter))
                {
                    context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", "管理 API 请求过于频繁");
                    return;
                }
                await next();
                if (!context.Response.HasStarted && context.Response.StatusCode >= 400 &&
                    context.Response.ContentLength is null && string.IsNullOrEmpty(context.Response.ContentType))
                {
                    await WriteErrorAsync(context, context.Response.StatusCode,
                        StatusCodeToErrorCode(context.Response.StatusCode),
                        GetDefaultMessage(context.Response.StatusCode));
                }
            }
            catch (BadHttpRequestException ex)
            {
                await WriteErrorAsync(context, ex.StatusCode, StatusCodeToErrorCode(ex.StatusCode), GetDefaultMessage(ex.StatusCode));
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json", "请求 JSON 格式无效",
                    new { field = ex.Path, line = ex.LineNumber });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "管理 API 未处理异常，请求 ID: {TraceId}", context.TraceIdentifier);
                if (!context.Response.HasStarted)
                    await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "管理 API 内部错误");
            }
            finally
            {
                if (write && settings.AuditMutatingRequests &&
                    !context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase))
                {
                    var username = context.Items["Username"]?.ToString() ?? "anonymous";
                    try
                    {
                        context.RequestServices.GetRequiredService<IAuditLogService>().Log(username,
                            $"Admin API {context.Request.Method} {context.Request.Path} => {context.Response.StatusCode} [{context.TraceIdentifier}]",
                            identity);
                    }
                    catch (Exception ex) { Logger.Error(ex, "管理审计写入失败，请求 ID: {TraceId}", context.TraceIdentifier); }
                }
            }
        });
        return app;
    }

    /// <summary>把公开的 /api/v1 路径转换为当前内部 /api 路由。</summary>
    public static bool TryRewriteVersionedPath(PathString path, out PathString rewritten)
    {
        if (path.StartsWithSegments(VersionPrefix, out var remaining))
        {
            rewritten = new PathString("/api").Add(remaining);
            return true;
        }
        rewritten = path;
        return false;
    }

    /// <summary>创建可供端点与中间件复用的标准错误结果。</summary>
    public static IResult Error(HttpContext context, int statusCode, string code, string message, object? details = null) =>
        Results.Json(CreateError(context, code, message, details), statusCode: statusCode);

    /// <summary>从当前请求创建稳定错误信封。</summary>
    public static ApiErrorEnvelope CreateError(HttpContext context, string code, string message, object? details = null) =>
        new(false, message, new ApiError(code, message, context.TraceIdentifier, details));

    /// <summary>根据当前端点数据源生成 OpenAPI 3.1 文档。</summary>
    public static object BuildOpenApiDocument(EndpointDataSource endpointDataSource)
    {
        var paths = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var endpoint in endpointDataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) continue;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods == null || methods.Count == 0) continue;

            var publicPath = VersionPrefix + ManagementOpenApiModels.Path(endpoint.RoutePattern)[4..];
            var operations = paths.TryGetValue(publicPath, out var existing)
                ? (SortedDictionary<string, object>)existing
                : new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var method in methods.OrderBy(x => x, StringComparer.Ordinal))
            {
                var runtimeAgent = raw.StartsWith("/api/runtime-state/agent/", StringComparison.OrdinalIgnoreCase);
                var nodeAgent = runtimeAgent || raw.StartsWith("/api/distribution/agent/", StringComparison.OrdinalIgnoreCase);
                var responses = new Dictionary<string, object>
                {
                        ["200"] = new { description = "请求成功（未声明响应模型）" },
                        ["400"] = ErrorResponse("请求无效"),
                        ["401"] = ErrorResponse("未认证"),
                        ["403"] = ErrorResponse("权限不足"),
                        ["428"] = ErrorResponse("需要二次身份验证"),
                        ["429"] = ErrorResponse("请求超过限额"),
                        ["500"] = ErrorResponse("服务器内部错误"),
                        ["503"] = ErrorResponse("服务暂不可用"),
                        ["404"] = ErrorResponse("资源不存在"),
                        ["409"] = ErrorResponse("状态或幂等键冲突"),
                        ["413"] = ErrorResponse("请求体超过限制")
                };
                var produced = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
                if (produced.Any(p => p.StatusCode is >= 200 and < 300)) responses.Remove("200");
                foreach (var response in produced.GroupBy(item => item.StatusCode))
                    responses[response.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = ManagementOpenApiModels.Responses(response);
                var operation = new Dictionary<string, object>
                {
                    ["operationId"] = CreateOperationId(method, publicPath),
                    ["responses"] = responses,
                    ["parameters"] = ManagementOpenApiModels.Parameters(endpoint.RoutePattern)
                        .Concat(method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? ManagementOpenApiModels.ListParameters(produced) : [])
                        .Concat(nodeAgent
                        ? new object[] {
                            new { name = "X-LyWaf-Timestamp", @in = "header", required = true, schema = new { type = "string" } },
                            new { name = "X-LyWaf-Nonce", @in = "header", required = true, schema = new { type = "string" } }
                        } : []).Concat(runtimeAgent ? new object[] {
                            new { name = "X-LyWaf-Node", @in = "header", required = true, schema = new { type = "string", maxLength = 128 } }
                        } : []).ToArray(),
                    ["x-permissions"] = IsAnonymous(raw) || nodeAgent ? [] : endpoint.Metadata.GetOrderedMetadata<LyWaf.Services.Auth.ManagementPermissionRequirement>()
                        .Select(p => p.Permission).Distinct().ToArray(),
                    ["security"] = IsAnonymous(raw) ? Array.Empty<object>() : new object[] { new Dictionary<string, string[]> { [runtimeAgent ? "runtimeStateSignature" : nodeAgent ? "nodeSignature" : "bearerAuth"] = [] } }
                };
                if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is { } accepts)
                    operation["requestBody"] = ManagementOpenApiModels.Request(accepts);
                operations[method.ToLowerInvariant()] = operation;
            }
            paths[publicPath] = operations;
        }

        return new
        {
            openapi = "3.1.0",
            info = new { title = "LyWaf Management API", version = CurrentVersion },
            servers = new[] { new { url = "/" } },
            paths,
            components = new
            {
                securitySchemes = new
                {
                    bearerAuth = new { type = "http", scheme = "bearer", bearerFormat = "JWT or LyWaf API Token" },
                    nodeSignature = new { type = "apiKey", @in = "header", name = "X-LyWaf-Signature", description = "HMAC-SHA256 of operation, timestamp, nonce and lowercase body SHA256 separated by newline; requires node shared secret." },
                    runtimeStateSignature = new { type = "apiKey", @in = "header", name = "X-LyWaf-Signature", description = "HMAC-SHA256 of runtime-state, node ID, timestamp, nonce and lowercase body SHA256 separated by newline; requires runtime-state peer secret." }
                },
                schemas = new
                {
                    ApiError = new
                    {
                        type = "object",
                        required = new[] { "code", "message", "traceId" },
                        properties = new
                        {
                            code = new { type = "string" }, message = new { type = "string" },
                            traceId = new { type = "string" }, details = new { type = "object" }
                        }
                    },
                    ApiErrorEnvelope = new
                    {
                        type = "object", required = new[] { "success", "message", "error" },
                        properties = new { success = new { type = "boolean", @const = false }, message = new { type = "string" }, error = SchemaReference("#/components/schemas/ApiError") }
                    }
                }
            }
        };
    }

    private static object ErrorResponse(string description) => new
    {
        description,
        content = new Dictionary<string, object> { ["application/json"] = new { schema = SchemaReference("#/components/schemas/ApiErrorEnvelope") } }
    };

    private static Dictionary<string, string> SchemaReference(string value) => new() { ["$ref"] = value };

    private static string CreateOperationId(string method, string path) =>
        method.ToLowerInvariant() + "_" + string.Join('_', path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim('{', '}', '*').Replace('-', '_')));

    private static bool IsAnonymous(string path) => path is "/api/auth/login" or "/api/auth/time" or "/api/auth/check" or "/api/health/ready" or "/api/pcap/ca.crl";

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message, object? details = null)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.Headers["X-LyWaf-Api-Version"] = CurrentVersion;
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(CreateError(context, code, message, details));
    }

    internal static string StatusCodeToErrorCode(int statusCode) => statusCode switch
    {
        400 => "invalid_request", 401 => "unauthorized", 403 => "permission_denied", 404 => "not_found",
        405 => "method_not_allowed", 409 => "conflict", 413 => "request_too_large", 429 => "rate_limited",
        415 => "unsupported_media_type", 422 => "validation_failed", 428 => "precondition_required",
        503 => "service_unavailable", _ => statusCode >= 500 ? "internal_error" : "request_failed"
    };

    private static string GetDefaultMessage(int statusCode) => statusCode switch
    {
        401 => "未授权访问", 403 => "禁止访问", 404 => "管理 API 不存在", 405 => "请求方法不受支持",
        413 => "请求体超过限制", 429 => "请求过于频繁", 503 => "服务暂不可用", _ => "请求失败"
    };
}
