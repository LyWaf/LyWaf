using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing.Patterns;

namespace LyWaf.Services.ManagementApi;

/// <summary>将管理端点的路由和 DTO 元数据转换为有界的 OpenAPI 模型。</summary>
internal static class ManagementOpenApiModels
{
    public static string Path(RoutePattern pattern) => "/" + string.Join('/', pattern.PathSegments.Select(segment =>
        string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternParameterPart parameter => "{" + parameter.Name + "}",
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            _ => ""
        }))));

    public static object[] Parameters(RoutePattern pattern) => pattern.Parameters.Select(parameter => (object)new
    {
        name = parameter.Name,
        @in = "path",
        required = true,
        schema = new { type = parameter.ParameterPolicies.Any(p => p.Content is "int" or "long") ? "integer" : "string" }
    }).ToArray();

    public static object[] ListParameters(IEnumerable<IProducesResponseTypeMetadata> responses)
    {
        if (!responses.Any(response => IsPage(response.Type))) return [];
        return
        [
            Query("page", new { type = "integer", minimum = 1, maximum = int.MaxValue, @default = 1 }),
            Query("pageSize", new { type = "integer", minimum = 1, maximum = 100, @default = 20 }),
            Query("sortBy", new { type = "string" }, "Business-specific allowlisted sort field; unsupported fields return 400."),
            Query("sortDirection", new { type = "string", @enum = new[] { "asc", "desc" } }),
            Query("search", new { type = "string", maxLength = 200 }, "Case-insensitive literal substring on the business search fields."),
            Query("from", new { type = "string", format = "date-time" }, "Inclusive lower timestamp bound. Lists without a time field reject time filters."),
            Query("to", new { type = "string", format = "date-time" }, "Inclusive upper timestamp bound; must not precede from.")
        ];
    }

    private static bool IsPage(Type? type)
    {
        if (type != null && new[] { "Data", "Total", "Page", "PageSize", "SortBy", "SortDirection" }
            .All(name => type.GetProperties().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))) return true;
        for (; type != null; type = type.BaseType)
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ManagementPage<>)) return true;
        return false;
    }

    private static object Query(string name, object schema, string description = "") => new
    {
        name, @in = "query", required = false, schema, description
    };

    public static object Request(IAcceptsMetadata metadata) => new
    {
        required = !metadata.IsOptional,
        content = metadata.ContentTypes.Distinct().ToDictionary(type => type, type => new { schema = Schema(metadata.RequestType) })
    };

    public static object Response(IProducesResponseTypeMetadata metadata)
    {
        var result = new Dictionary<string, object> { ["description"] = $"HTTP {metadata.StatusCode}" };
        if (metadata.Type is not null && metadata.Type != typeof(void) && metadata.StatusCode != 204)
            result["content"] = (metadata.ContentTypes.Any() ? metadata.ContentTypes : ["application/json"])
                .Distinct().ToDictionary(type => type, type => new { schema = metadata.Type == typeof(byte[]) &&
                    !type.Contains("json", StringComparison.OrdinalIgnoreCase)
                        ? (object)new { type = "string", format = "binary" } : Schema(metadata.Type) });
        return result;
    }

    public static object Responses(IEnumerable<IProducesResponseTypeMetadata> metadata)
    {
        var variants = metadata.GroupBy(item => (item.Type, string.Join(',', item.ContentTypes))).Select(group => group.First()).ToArray();
        if (variants.Length == 1) return Response(variants[0]);
        var content = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in variants)
        {
            var response = JsonSerializer.SerializeToElement(Response(variant));
            if (!response.TryGetProperty("content", out var types)) continue;
            foreach (var media in types.EnumerateObject())
            {
                if (!content.TryGetValue(media.Name, out var schemas)) content.Add(media.Name, schemas = []);
                schemas.Add(media.Value.GetProperty("schema").Clone());
            }
        }
        var result = new Dictionary<string, object> { ["description"] = $"HTTP {variants[0].StatusCode}" };
        if (content.Count > 0)
            result["content"] = content.ToDictionary(item => item.Key, item => new
            {
                schema = item.Value.Count == 1 ? (object)item.Value[0] : new { anyOf = item.Value }
            });
        return result;
    }

    private static object Schema(Type? type, int depth = 0)
    {
        if (type is null || type == typeof(object) || depth > 8) return new { };
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return new { anyOf = new[] { Schema(underlying, depth + 1), new { type = "null" } } };
        if (type == typeof(string) || type == typeof(char)) return new { type = "string" };
        if (type == typeof(bool)) return new { type = "boolean" };
        if (type == typeof(Guid)) return new { type = "string", format = "uuid" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return new { type = "string", format = "date-time" };
        if (type == typeof(DateOnly)) return new { type = "string", format = "date" };
        if (type == typeof(TimeOnly)) return new { type = "string", format = "time" };
        if (type == typeof(TimeSpan)) return new { type = "string", pattern = @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$",
            description = ".NET constant TimeSpan format: [-][d.]hh:mm:ss[.fffffff]" };
        if (type == typeof(Uri)) return new { type = "string", format = "uri-reference" };
        if (type == typeof(byte[])) return new { type = "string", contentEncoding = "base64" };
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().Select(value => JsonSerializer.SerializeToElement(value, type)).ToArray();
            return new { type = values.FirstOrDefault().ValueKind == JsonValueKind.String ? "string" : "integer", @enum = values };
        }
        if (type.IsPrimitive || type == typeof(decimal))
            return new { type = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer" };
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary != null) return new { type = "object", additionalProperties = Schema(dictionary.GetGenericArguments()[1], depth + 1) };
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return new { type = "array", items = Schema(type.IsArray ? type.GetElementType() : enumerable?.GetGenericArguments()[0], depth + 1) };
        }
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0 &&
                p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray();
        return new
        {
            type = "object",
            properties = properties.ToDictionary(Name, p => PropertySchema(p, depth + 1)),
            required = properties.Where(p => p.GetCustomAttribute<RequiredAttribute>() != null ||
                p.GetCustomAttribute<JsonRequiredAttribute>() != null).Select(Name).ToArray()
        };
    }

    private static string Name(PropertyInfo property) => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static object PropertySchema(PropertyInfo property, int depth)
    {
        var schema = JsonSerializer.SerializeToElement(Schema(property.PropertyType, depth))
            .EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        if (property.GetCustomAttribute<StringLengthAttribute>() is { } length)
        {
            schema["minLength"] = length.MinimumLength;
            schema["maxLength"] = length.MaximumLength;
        }
        var stringValue = property.PropertyType == typeof(string);
        var dictionaryValue = property.PropertyType.GetInterfaces().Append(property.PropertyType).Any(type =>
            type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                   type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (property.GetCustomAttribute<MinLengthAttribute>() is { } min)
            schema[stringValue ? "minLength" : dictionaryValue ? "minProperties" : "minItems"] = min.Length;
        if (property.GetCustomAttribute<MaxLengthAttribute>() is { } max)
            schema[stringValue ? "maxLength" : dictionaryValue ? "maxProperties" : "maxItems"] = max.Length;
        if (property.GetCustomAttribute<RegularExpressionAttribute>() is { } pattern)
            schema["pattern"] = pattern.Pattern;
        if (property.GetCustomAttribute<EnumNameAttribute>() is { } enumName)
        {
            schema["examples"] = Enum.GetNames(enumName.EnumType);
            schema["description"] = "Case-insensitive enum name; numeric values are not accepted.";
            schema["pattern"] = "^(?:" + string.Join('|', Enum.GetNames(enumName.EnumType).Select(name =>
                string.Concat(name.Select(character => char.IsAsciiLetter(character)
                    ? $"[{char.ToLowerInvariant(character)}{char.ToUpperInvariant(character)}]"
                    : System.Text.RegularExpressions.Regex.Escape(character.ToString()))))) + ")$";
        }
        if (property.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            if (decimal.TryParse(Convert.ToString(range.Minimum, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minimum)) schema["minimum"] = minimum;
            if (decimal.TryParse(Convert.ToString(range.Maximum, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maximum)) schema["maximum"] = maximum;
        }
        if (!property.PropertyType.IsValueType && property.GetCustomAttribute<RequiredAttribute>() == null &&
            new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable)
            return new { anyOf = new object[] { schema, new { type = "null" } } };
        return schema;
    }
}
