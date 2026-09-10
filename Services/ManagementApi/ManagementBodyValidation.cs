using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;

namespace LyWaf.Services.ManagementApi;

/// <summary>对已绑定 JSON DTO 执行有界字段校验，返回字段路径，不返回提交的敏感值。</summary>
internal static class ManagementBodyValidation
{
    public static Dictionary<string, string[]> Validate(EndpointFilterInvocationContext context)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var metadata = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>();
        if (metadata?.RequestType is not { } bodyType || !metadata.ContentTypes.Any(t => t.Contains("json", StringComparison.OrdinalIgnoreCase))) return errors;
        var body = context.Arguments.FirstOrDefault(arg => arg != null && bodyType.IsInstanceOfType(arg));
        if (body == null)
        {
            if (!metadata.IsOptional) errors["$body"] = ["请求体不能为空"];
            return errors;
        }
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var nullability = new NullabilityInfoContext();
        var visited = 0;
        Visit(body, "", 0, body is IDictionary && bodyType.GetGenericArguments() is [_, var dictionaryValue] && dictionaryValue == typeof(object));
        return errors;

        void Visit(object? value, string path, int depth, bool allowNullElements = false)
        {
            if (value == null || errors.Count >= 20) return;
            if (++visited > 20000 || depth > 12) { errors.TryAdd(path.Length == 0 ? "$body" : path, ["请求结构超过校验限制"]); return; }
            var type = value.GetType();
            if (type.IsEnum)
            {
                if (type.GetCustomAttribute<FlagsAttribute>() == null && !Enum.IsDefined(type, value)) errors.TryAdd(path, ["枚举值无效"]);
                return;
            }
            if (type.IsPrimitive || value is string or decimal or DateTime or DateTimeOffset or Guid or JsonElement) return;
            if (!type.IsValueType && !seen.Add(value)) return;
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value == null && !allowNullElements) errors.TryAdd(path + "[entry]", ["字典值不能为空"]);
                    else Visit(entry.Value, path + "[entry]", depth + 1);
                    if (errors.Count >= 20 || visited > 20000) break;
                }
                return;
            }
            if (value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (item == null && !allowNullElements) errors.TryAdd(path + "[" + index + "]", ["列表条目不能为空"]);
                    else Visit(item, path + "[" + index + "]", depth + 1);
                    index++;
                    if (errors.Count >= 20 || visited > 20000) break;
                }
                return;
            }
            var validation = new List<ValidationResult>();
            Validator.TryValidateObject(value, new ValidationContext(value), validation, true);
            foreach (var failure in validation)
                foreach (var member in failure.MemberNames.DefaultIfEmpty("$body"))
                    errors.TryAdd(Prefix(JsonMemberPath(type, member)), ["字段不符合约束"]);
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true || property.SetMethod == null ||
                    property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                var member = Prefix(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                var item = property.GetValue(value);
                var declared = nullability.Create(property);
                if (item == null && declared.ReadState == NullabilityState.NotNull) errors.TryAdd(member, ["字段不能为空"]);
                else Visit(item, member, depth + 1, declared.ElementType?.ReadState == NullabilityState.Nullable ||
                    declared.GenericTypeArguments is [{ ReadState: NullabilityState.Nullable }] or [_, { ReadState: NullabilityState.Nullable }]);
                if (errors.Count >= 20 || visited > 20000) break;
            }
            string Prefix(string member) => path.Length == 0 ? member : path + "." + member;
        }
    }

    private static string JsonMemberPath(Type type, string path)
    {
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var bracket = parts[i].IndexOf('[');
            var name = bracket < 0 ? parts[i] : parts[i][..bracket];
            var suffix = bracket < 0 ? "" : parts[i][bracket..];
            var property = type.GetProperty(name);
            parts[i] = (property?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(name)) + suffix;
            type = property?.PropertyType ?? typeof(object);
            if (bracket >= 0) type = type.GetElementType() ?? type.GetGenericArguments().LastOrDefault() ?? typeof(object);
        }
        return string.Join('.', parts);
    }
}
