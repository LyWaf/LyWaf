using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Plugins.Core;

/// <summary>Describes one configuration field; collection elements use a wildcard segment.</summary>
public sealed record PluginConfigurationField(string Path, string Type, string Label, bool Required,
    string? Minimum, string? Maximum, bool Sensitive, bool RequiresRestart, PluginConfigurationScope Scopes,
    string? LabelKey = null, string? DescriptionKey = null);

/// <summary>Builds configuration metadata and validates a detached candidate before persistence.</summary>
public static class PluginConfigurationSchema
{
    public static IReadOnlyList<PluginConfigurationField> Describe(Type type)
    {
        var fields = new List<PluginConfigurationField>();
        Visit(type, "", 0, false);
        return fields;

        void Visit(Type current, string prefix, int depth, bool sensitive)
        {
            if (depth > 12) return;
            foreach (var property in Properties(current))
            {
                if (property.GetMethod == null || property.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;
                var path = prefix + Key(property);
                var secret = sensitive || IsSensitive(property);
                var range = property.GetCustomAttribute<RangeAttribute>();
                var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                var metadata = property.GetCustomAttribute<PluginConfigurationAttribute>();
                var labelKey = metadata?.LabelKey ?? (LyWaf.Localization.TextLocalizer.HasKey("fields." + Key(property)) ? "fields." + Key(property) : null);
                fields.Add(new(path, valueType.Name,
                    labelKey != null && LyWaf.Localization.TextLocalizer.HasKey(labelKey) ? LyWaf.Localization.TextLocalizer.Get(labelKey) : property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? Key(property),
                    property.IsDefined(typeof(RequiredAttribute)), range?.Minimum.ToString(), range?.Maximum.ToString(),
                    secret, property.GetCustomAttribute<PluginConfigurationAttribute>()?.RequiresRestart ?? false,
                    metadata?.Scopes ?? PluginConfigurationScope.All, labelKey, metadata?.DescriptionKey));
                var element = ElementType(valueType);
                if (element != null && !IsScalar(element)) Visit(element, path + ":*:", depth + 1, secret);
                else if (!IsScalar(valueType) && element == null) Visit(valueType, path + ":", depth + 1, secret);
            }
        }
    }

    public static bool SensitivePath(Type type, string path)
    {
        var parts = path.Split(':');
        for (var i = 0; i < parts.Length; i++)
        {
            var element = ElementType(type);
            if (element != null)
            {
                var isElementKey = typeof(IDictionary).IsAssignableFrom(type) || int.TryParse(parts[i], out _) || parts[i] == "*";
                type = element;
                if (isElementKey) continue;
            }
            var property = Properties(type).FirstOrDefault(p => Key(p).Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (property == null) return false;
            if (IsSensitive(property)) return true;
            type = property.PropertyType;
        }
        return false;
    }

    public static Dictionary<string, string[]> Validate(Type type, IReadOnlyDictionary<string, string> values)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (values.Count > 2048) { errors["$body"] = ["配置字段最多 2048 项"]; return errors; }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, value) in values)
        {
            if (!keys.Add(path)) errors[path] = ["配置字段重复（不区分大小写）"];
            var parts = path.Split(':');
            if (path.Length > 512 || parts.Length > 12 || parts.Any(string.IsNullOrWhiteSpace) || value == null || value.Length > 65536)
                errors[path] = ["配置字段或值超过限制"];
            else if (value == "" && CollectionPath(type, parts)) continue;
            else if (Resolve(type, parts, 0) is not { } fieldType) errors[path] = ["未知配置字段或无效集合索引"];
            else
            {
                try
                {
                    var converted = TypeDescriptor.GetConverter(fieldType).ConvertFromInvariantString(value);
                    var actualType = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
                    if (actualType.IsEnum && converted != null && !actualType.IsDefined(typeof(FlagsAttribute)) && !Enum.IsDefined(actualType, converted))
                        errors[path] = ["枚举值无效"];
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
                { errors[path] = ["字段类型不匹配"]; }
            }
        }
        if (errors.Count > 0) return errors;
        try
        {
            var candidate = Bind(type, values);
            return ManagementBodyValidation.ValidateBody(candidate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or TargetInvocationException)
        {
            errors["$body"] = ["配置类型转换失败，请检查数字、布尔值、枚举或时长格式"];
            return errors;
        }
    }

    public static object Bind(Type type, IReadOnlyDictionary<string, string> values)
    {
        var candidate = Activator.CreateInstance(type)!;
        ClearSupplied(candidate, "", values.Keys.ToArray(), 0);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values
            .Where(p => !(p.Value == "" && CollectionPath(type, p.Key.Split(':'))))
            .Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();
        try { configuration.Bind(candidate); }
        finally { (configuration as IDisposable)?.Dispose(); }
        return candidate;
    }

    private static bool CollectionPath(Type type, string[] parts)
    {
        foreach (var part in parts)
        {
            var element = ElementType(type);
            if (element != null)
            {
                if (!typeof(IDictionary).IsAssignableFrom(type) &&
                    (!int.TryParse(part, out var index) || index < 0 || index >= 2048 || index.ToString() != part)) return false;
                type = element; continue;
            }
            var property = Properties(type).FirstOrDefault(p => Key(p).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (property == null) return false;
            type = property.PropertyType;
        }
        return ElementType(type) != null;
    }

    private static Type? Resolve(Type type, string[] parts, int index)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (index == parts.Length) return IsScalar(type) ? type : null;
        var element = ElementType(type);
        if (element != null)
        {
            var dictionary = typeof(IDictionary).IsAssignableFrom(type);
            if (!dictionary && (!int.TryParse(parts[index], out var n) || n < 0 || n >= 2048 || n.ToString() != parts[index])) return null;
            return Resolve(element, parts, index + 1);
        }
        var property = Properties(type).FirstOrDefault(p => Key(p).Equals(parts[index], StringComparison.OrdinalIgnoreCase));
        return property != null ? Resolve(property.PropertyType, parts, index + 1) : null;
    }

    private static void ClearSupplied(object value, string prefix, string[] keys, int depth)
    {
        if (depth > 12) return;
        foreach (var property in Properties(value.GetType()).Where(p => p.GetMethod != null))
        {
            var path = prefix + Key(property) + ":";
            if (!keys.Any(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase) || k.Equals(path.TrimEnd(':'), StringComparison.OrdinalIgnoreCase))) continue;
            var child = property.GetValue(value);
            if (child is Array array) property.SetValue(value, Array.CreateInstance(array.GetType().GetElementType()!, 0));
            else if (child is IList list) list.Clear();
            else if (child is IDictionary dictionary) dictionary.Clear();
            else if (child != null && !IsScalar(child.GetType())) ClearSupplied(child, path, keys, depth + 1);
        }
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.SetMethod?.IsPublic == true && p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0);
    private static string Key(PropertyInfo p) => p.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? p.Name;
    private static bool IsScalar(Type type) => TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string));
    private static Type? ElementType(Type type) => type.IsArray ? type.GetElementType() :
        typeof(IDictionary).IsAssignableFrom(type) ? type.GetGenericArguments().LastOrDefault() :
        typeof(IList).IsAssignableFrom(type) ? type.GetGenericArguments().FirstOrDefault() : null;
    private static bool IsSensitive(PropertyInfo p) => p.GetCustomAttribute<PluginConfigurationAttribute>()?.Sensitive == true ||
        p.GetCustomAttribute<PasswordPropertyTextAttribute>()?.Password == true ||
        new[] { "password", "secret", "token", "privatekey" }.Any(s => Key(p).Contains(s, StringComparison.OrdinalIgnoreCase));
}
