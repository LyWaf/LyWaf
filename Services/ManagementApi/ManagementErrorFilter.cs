using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyWaf.Services.ManagementApi;

/// <summary>保留业务错误字段，同时补充统一错误码与追踪信息。</summary>
public sealed class ManagementErrorFilter : IEndpointFilter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var validation = ManagementBodyValidation.Validate(context);
        if (validation.Count != 0)
            return ManagementApiContract.Error(context.HttpContext, 400, "validation_failed", "请求字段校验失败", validation);
        object? result;
        try { result = await next(context); }
        catch (LyWaf.Plugins.Core.PluginPermissionException ex)
        {
            return ManagementApiContract.Error(context.HttpContext, 403, "plugin_permission_denied", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ManagementApiContract.Error(context.HttpContext, 400, "validation_failed", ex.Message,
                string.IsNullOrEmpty(ex.ParamName) ? null : new Dictionary<string, string[]>
                { [JsonNamingPolicy.CamelCase.ConvertName(ex.ParamName)] = [ex.Message] });
        }
        if (result is not IValueHttpResult { Value: not null } value || value.Value is ApiErrorEnvelope) return result;
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status >= 500)
            return ManagementApiContract.Error(context.HttpContext, status, ManagementApiContract.StatusCodeToErrorCode(status), "管理服务暂时无法完成请求，请按请求 ID 查询服务端日志");
        // 只处理错误响应，正常列表和流式结果不复制到内存。
        if (status < 400)
        {
            var success = value.Value.GetType().GetProperty("success") ?? value.Value.GetType().GetProperty("Success");
            if (success?.PropertyType != typeof(bool) || success.GetValue(value.Value) is not false) return result;
            status = 400;
        }
        var body = JsonSerializer.SerializeToNode(value.Value, Json) as JsonObject;
        if (body == null) return result;
        if (body["error"] is JsonObject existing && existing["code"] != null) return result;
        var message = body["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var text)
            ? text : body["errorMessage"] is JsonValue errorValue && errorValue.TryGetValue<string>(out var errorText)
                ? errorText : "请求未能完成";
        var code = ManagementApiContract.StatusCodeToErrorCode(status);
        body["success"] = false;
        body["message"] = message;
        body["error"] = JsonSerializer.SerializeToNode(new ApiError(code, message, context.HttpContext.TraceIdentifier), Json);
        return Results.Json(body, statusCode: status);
    }
}
