using System.Globalization;
using System.Text.Json;
using LyWaf.Localization;

namespace LyWaf.Plugins.Core;

/// <summary>Loads only resources owned by the plugin assembly; never merges them into host messages.</summary>
public static class PluginLocalization
{
    public static Dictionary<string, Dictionary<string, string>> Load(ILyWafPlugin plugin)
    {
        var type = plugin.GetType();
        var prefix = plugin.Metadata.LocalizationResourcePrefix ?? type.Namespace + ".Localization";
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            using var stream = type.Assembly.GetManifestResourceStream(prefix + "." + language + ".json");
            if (stream == null) continue;
            result[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        return result;
    }

    public static string? Get(Dictionary<string, Dictionary<string, string>> resources, string key)
    {
        var language = TextLocalizer.Normalize(CultureInfo.CurrentUICulture.Name);
        return resources.GetValueOrDefault(language)?.GetValueOrDefault(key)
            ?? resources.GetValueOrDefault("zh-CN")?.GetValueOrDefault(key);
    }

    public static PluginConfigurationField Localize(PluginConfigurationField field, Dictionary<string, Dictionary<string, string>> resources)
    {
        var key = field.LabelKey ?? "fields." + field.Path.Split(':')[^1];
        return field with { Label = Get(resources, key) ?? field.Label, LabelKey = key };
    }
}
