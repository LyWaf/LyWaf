using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LyWaf.Config;
using YamlDotNet.Serialization;

namespace LyWaf.Services.Distribution;

/// <summary>Only reviewed business sections are distributable; everything else belongs to the node.</summary>
public static class NodeLocalConfigurationOverlay
{
    private static readonly HashSet<string> SharedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReverseProxy", "Protect", "Statistic", "SpeedLimit", "SimpleRes", "ABTest", "RequestMirroring"
    };

    // Metadata is covered by the source digest and is never applied as business configuration.
    public static void ValidateCompatibility(string path, string content)
    {
        var parsed = Parse(content, path.EndsWith(".ly", StringComparison.OrdinalIgnoreCase));
        if (!parsed.TryGetValue("DistributionCompatibility", out var value)) return; // Legacy schema 1.
        if (value is not Dictionary<string, object> contract || contract.Keys.Any(key =>
            key is not ("SchemaVersion" or "MinimumAppVersion" or "MaximumAppVersion")))
            throw new InvalidOperationException("Invalid distribution compatibility contract");
        if (Convert.ToString(contract.GetValueOrDefault("SchemaVersion"), CultureInfo.InvariantCulture) != "1")
            throw new InvalidOperationException("Unsupported configuration schema version");
        var current = typeof(NodeLocalConfigurationOverlay).Assembly.GetName().Version!;
        Version? minimum = null, maximum = null;
        foreach (var key in new[] { "MinimumAppVersion", "MaximumAppVersion" })
        {
            if (!contract.TryGetValue(key, out var raw)) continue;
            if (!Version.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out var version))
                throw new InvalidOperationException("Invalid configuration application version");
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            if (key == "MinimumAppVersion") minimum = version; else maximum = version;
        }
        if ((minimum is not null && maximum is not null && minimum > maximum)
            || (minimum is not null && minimum > current) || (maximum is not null && maximum < current))
            throw new InvalidOperationException("Configuration application version is incompatible");
    }

    public static string Compose(string path, string localContent, string remoteContent)
    {
        var ly = Path.GetExtension(path).Equals(".ly", StringComparison.OrdinalIgnoreCase);
        if (!ly && Path.GetExtension(path).ToLowerInvariant() is not (".yaml" or ".yml"))
            throw new InvalidOperationException("Node distribution supports .ly, .yaml and .yml only");
        var local = Parse(localContent, ly);
        var remote = Parse(remoteContent, ly);
        var combined = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in remote)
            if (SharedRaw(pair.Key, ly)) combined.Add(pair.Key, pair.Value);
        foreach (var pair in local)
            if (!SharedRaw(pair.Key, ly)) combined.Add(pair.Key, pair.Value);
        foreach (var field in new[] { "CheckArgsFile", "CheckPostFile" })
        {
            var localProtect = local.GetValueOrDefault("Protect") as Dictionary<string, object>;
            var remoteProtect = combined.GetValueOrDefault("Protect") as Dictionary<string, object>;
            if (remoteProtect is not null) remoteProtect.Remove(field);
            if (localProtect?.TryGetValue(field, out var value) == true)
            {
                if (remoteProtect is null) combined["Protect"] = remoteProtect = new(StringComparer.OrdinalIgnoreCase);
                remoteProtect[field] = value;
            }
        }
        var expected = Effective(combined, ly);
        var localEffective = Effective(local, ly);
        // Site shorthand can synthesize listeners, certificates and filesystem paths.
        // Refuse those changes even though the original root appeared to be a business site.
        foreach (var key in expected.Keys.Concat(localEffective.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            if ((!SharedRoots.Contains(key.Split(':')[0]) || key.Equals("Protect:CheckArgsFile", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Protect:CheckPostFile", StringComparison.OrdinalIgnoreCase))
                && expected.GetValueOrDefault(key) != localEffective.GetValueOrDefault(key))
                throw new InvalidOperationException("Distributed site changes node-local settings; provision listeners and filesystem settings locally first");
        if (!ly && Equal(expected, Effective(Parse(remoteContent, ly), ly))) return remoteContent;
        var output = ly ? WriteLy(combined) : new SerializerBuilder().Build().Serialize(combined);
        if (!Equal(expected, Effective(Parse(output, ly), ly)))
            throw new InvalidOperationException("Configuration cannot be safely round-tripped for node overlay");
        return output;
    }

    internal static ConfigurationRoot ReadConfiguration(string path, string content) => (ConfigurationRoot)new ConfigurationBuilder()
        .AddInMemoryCollection(Effective(Parse(content, path.EndsWith(".ly", StringComparison.OrdinalIgnoreCase)),
            path.EndsWith(".ly", StringComparison.OrdinalIgnoreCase))).Build();

    private static bool SharedRaw(string key, bool ly) => SharedRoots.Contains(key)
        || (ly && (key.StartsWith(':') || key.Contains('.') || key.Contains("/", StringComparison.Ordinal)));

    private static Dictionary<string, object> Parse(string content, bool ly)
    {
        if (ly)
        {
            // Imports read arbitrary files during parsing. No import is permitted in distributed or baseline input.
            var tokens = Regex.Matches(content, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|#[^\\r\\n]*|\\bimport\\b");
            if (tokens.Any(match => match.Value == "import"))
                throw new InvalidOperationException("Node overlay requires import-free .ly configuration");
            return Normalize(LyConfigParser.Parse(content));
        }
        var value = new DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<object>(content);
        return value is IDictionary dictionary ? Normalize(dictionary)
            : throw new InvalidOperationException("Configuration root must be a mapping");
    }

    private static Dictionary<string, object> Normalize(IDictionary source)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry pair in source)
        {
            var key = Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? "";
            if (key.Contains(':') && !key.StartsWith(':') && !key.Contains("//", StringComparison.Ordinal))
                if (!Regex.IsMatch(key, @"^[A-Za-z0-9.*-]+:\d+$"))
                    throw new InvalidOperationException("Colon-delimited configuration keys are not supported by node overlay");
            if (!result.TryAdd(key, NormalizeValue(pair.Value)!))
                throw new InvalidOperationException("Case-insensitive duplicate configuration key");
        }
        return result;
    }
    private static object? NormalizeValue(object? value) => value switch
    {
        IDictionary dictionary => Normalize(dictionary),
        IList list => list.Cast<object?>().Select(NormalizeValue).ToList(),
        _ => value
    };

    private static Dictionary<string, string?> Effective(Dictionary<string, object> parsed, bool ly)
    {
        var root = ly ? LyToAppSettingsConverter.TransformToAppSettings(parsed) : parsed;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Flatten(string key, object? value)
        {
            if (value is IDictionary dictionary)
                foreach (DictionaryEntry item in dictionary) Flatten(key.Length == 0 ? item.Key.ToString()! : key + ":" + item.Key, item.Value);
            else if (value is IList list)
                for (var i = 0; i < list.Count; i++) Flatten(key + ":" + i, list[i]);
            else if (!result.TryAdd(key, Convert.ToString(value, CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("Ambiguous effective configuration key");
        }
        Flatten("", root);
        return result;
    }
    private static bool Equal(Dictionary<string, string?> first, Dictionary<string, string?> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string WriteLy(Dictionary<string, object> root)
    {
        var text = new StringBuilder();
        string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        string Value(object? value) => value switch
        {
            null => throw new InvalidOperationException("Null .ly values cannot be safely rendered"),
            bool boolean => boolean ? "true" : "false",
            IDictionary => throw new InvalidOperationException("Object arrays are not supported by node .ly overlay"),
            IList list => "[" + string.Join(", ", list.Cast<object?>().Select(Value)) + "]",
            _ => QuoteLiteral(Convert.ToString(value, CultureInfo.InvariantCulture)!)
        };
        string QuoteLiteral(string value)
        {
            if (Regex.IsMatch(value, @"\$(?:\{|[A-Za-z_])"))
                throw new InvalidOperationException("Unresolved variable-like literals cannot be safely frozen in node .ly overlay");
            return Quote(value);
        }
        void Write(IDictionary dictionary, string indent)
        {
            foreach (DictionaryEntry pair in dictionary)
            {
                var key = pair.Key.ToString()!;
                if (!Regex.IsMatch(key, @"^[\p{L}_/*:][\p{L}\p{N}_./*:@ -]*$"))
                    throw new InvalidOperationException("Configuration key cannot be safely rendered as .ly");
                text.Append(indent).Append(key);
                if (pair.Value is IDictionary nested)
                {
                    text.AppendLine(" {"); Write(nested, indent + "    "); text.Append(indent).AppendLine("}");
                }
                else text.Append(" = ").AppendLine(Value(pair.Value));
            }
        }
        Write(root, "");
        return text.ToString();
    }
}
