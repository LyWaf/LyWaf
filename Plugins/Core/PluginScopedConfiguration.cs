using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Primitives;

namespace LyWaf.Plugins.Core;

/// <summary>Redacted effective values and their winning configuration levels for console inspection.</summary>
public sealed record PluginEffectiveConfiguration(string PluginId, string Host, string Path,
    Dictionary<string, string> Config, Dictionary<string, string> Sources, string? LastReloadError);

/// <summary>Validated plugin-only snapshots. A rejected reload keeps the preceding snapshot.</summary>
public sealed class PluginScopedConfiguration : IPluginRequestConfiguration, IDisposable
{
    private sealed record Values(Type Type, Dictionary<string, string> Fields, Dictionary<string, string> Sources);
    private sealed record Route(string Id, string Path, Dictionary<string, Values> Plugins);
    private sealed record Site(string Id, string[] Hosts, Dictionary<string, Values> Plugins, Route[] Routes);
    private sealed record Snapshot(Dictionary<string, Values> Global, Site[] Sites);
    private readonly IConfiguration _configuration;
    private readonly PluginMetadata[] _metadata;
    private readonly ILogger<PluginScopedConfiguration> _logger;
    private readonly string[]? _load;
    private readonly IDisposable _subscription;
    private readonly object _reloadLock = new();
    private Snapshot _snapshot;
    public string? LastReloadError { get; private set; }

    public PluginScopedConfiguration(IConfiguration configuration, IEnumerable<PluginMetadata> metadata,
        ILogger<PluginScopedConfiguration> logger)
    {
        _configuration = configuration;
        _metadata = metadata.ToArray();
        _logger = logger;
        _load = ReadLoad();
        _snapshot = Build();
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    public T Get<T>(HttpContext context, string pluginId) where T : class
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var key = typeof(PluginScopedConfiguration);
        // Keep all scoped plugin reads consistent within one request, even across a reload.
        if (context.Items.TryGetValue(key, out var existing)) snapshot = (Snapshot)existing!;
        else context.Items[key] = snapshot;
        var values = Select(snapshot, pluginId, context.Request.Host.Host, context.Request.Path.Value ?? "/");
        if (values.Type != typeof(T)) throw new InvalidOperationException("Plugin options type does not match metadata.");
        return (T)PluginConfigurationSchema.Bind(values.Type, values.Fields);
    }

    public PluginEffectiveConfiguration Describe(string pluginId, string host, string path)
    {
        var values = Select(Volatile.Read(ref _snapshot), pluginId, host, path);
        var visible = values.Fields.Where(p => !PluginConfigurationSchema.SensitivePath(values.Type, p.Key)).ToArray();
        return new(pluginId, host, path, visible.ToDictionary(p => p.Key, p => p.Value),
            visible.ToDictionary(p => p.Key, p => values.Sources[p.Key]), LastReloadError);
    }

    private static Values Select(Snapshot snapshot, string id, string host, string path)
    {
        // Exact host beats wildcard, then the longest suffix. Paths use longest matching prefix.
        var site = snapshot.Sites.Select(s => (Site: s, Score: s.Hosts.Select(h => HostScore(h, host)).Max()))
            .Where(s => s.Score >= 0).OrderByDescending(s => s.Score).FirstOrDefault().Site;
        var route = site?.Routes.Where(r => PathMatches(r.Path, path))
            .OrderByDescending(r => r.Path.TrimEnd('*').Length).ThenBy(r => r.Path.EndsWith("/**")).FirstOrDefault();
        var plugins = route?.Plugins ?? site?.Plugins ?? snapshot.Global;
        return plugins.TryGetValue(id, out var values) ? values : throw new KeyNotFoundException("Plugin does not support scoped configuration.");
    }

    private string[]? ReadLoad(IConfiguration? configuration = null)
    {
        var section = (configuration ?? _configuration).GetSection("Plugins:Load");
        return section.Exists() ? section.GetChildren().Select(c => c.Value ?? "").ToArray() : null;
    }

    public string ValidateGlobalCandidate(string pluginId, IReadOnlyDictionary<string, string> fields)
    {
        var prefix = $"PluginConfigSnapshots:{pluginId}:";
        var values = _configuration.AsEnumerable().Where(p => p.Value != null &&
            !p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields) values[prefix + field.Key] = field.Value;
        using var candidate = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return Fingerprint(Build(candidate));
    }

    public void EnsureApplied(string expectedFingerprint)
    {
        if (LastReloadError != null) throw new InvalidOperationException(LastReloadError);
        if (Fingerprint(Volatile.Read(ref _snapshot)) != expectedFingerprint)
            throw new InvalidOperationException("运行中的插件配置与待发布配置不一致");
    }

    private static string Fingerprint(Snapshot snapshot)
    {
        object DescribeValues(Dictionary<string, Values> values) => values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new { Id = p.Key, Fields = p.Value.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase) });
        var value = new { Global = DescribeValues(snapshot.Global), Sites = snapshot.Sites.Select(s => new {
            s.Id, s.Hosts, Plugins = DescribeValues(s.Plugins), Routes = s.Routes.Select(r => new { r.Id, r.Path, Plugins = DescribeValues(r.Plugins) }) }) };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private Snapshot Build(IConfiguration? candidate = null)
    {
        var source = candidate ?? _configuration;
        var load = ReadLoad(source);
        if ((_load == null) != (load == null) || !Enumerable.SequenceEqual(_load ?? [], load ?? [], StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Plugins:Load changes require restart.");
        if (source["Plugins:Enabled"]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            return new(new(StringComparer.OrdinalIgnoreCase), []);
        foreach (var id in _load ?? [])
            if (!_metadata.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Plugins:Load: unknown or unavailable plugin '{id}'.");
        var global = new Dictionary<string, Values>(StringComparer.OrdinalIgnoreCase);
        foreach (var metadata in _metadata.Where(m => m.SupportsScopedConfiguration))
        {
            var defaults = metadata.DefaultOptions ?? throw new InvalidOperationException($"{metadata.Id}: missing options model.");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            FlattenDefaults(Activator.CreateInstance(defaults.GetType())!, "", fields);
            var value = new Values(defaults.GetType(), fields, fields.Keys.ToDictionary(k => k, _ => "default", StringComparer.OrdinalIgnoreCase));
            var published = source.GetSection($"PluginConfigSnapshots:{metadata.Id}");
            if (published.Exists()) global[metadata.Id] = Merge(value, published, PluginConfigurationScope.Global, "global");
            else
            {
                value = Merge(value, source.GetSection($"Plugins:PluginConfigs:{metadata.Id}"), PluginConfigurationScope.Global, "global");
                global[metadata.Id] = Merge(value, source.GetSection($"Plugins:{metadata.Id}"), PluginConfigurationScope.Global, "global");
            }
        }
        var sites = new List<Site>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in source.GetSection("Sites").GetChildren())
        {
            CheckKeys(section, "Hosts", "Plugins", "Routes");
            var names = section.GetSection("Hosts").GetChildren().Select(c => c.Value?.Trim().ToLowerInvariant() ?? "").ToArray();
            if (names.Length == 0 || names.Any(h => !ValidHost(h) || !hosts.Add(h)))
                throw new InvalidOperationException($"{section.Path}: Hosts must be unique hostnames, optionally starting with '*.'.");
            var plugins = MergePlugins(global, section.GetSection("Plugins"), PluginConfigurationScope.Site, $"site:{section.Key}");
            var routes = new List<Route>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in section.GetSection("Routes").GetChildren())
            {
                CheckKeys(route, "Path", "Plugins");
                var path = route["Path"] ?? "";
                if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#') || path.Contains('\\') ||
                    path.Replace("/**", "").Contains('*') || (path.Contains("/**") && !path.EndsWith("/**")) || !paths.Add(path))
                    throw new InvalidOperationException($"{route.Path}: Path must be an exact path or a trailing '/**' prefix, without duplicates.");
                routes.Add(new(route.Key, path, MergePlugins(plugins, route.GetSection("Plugins"), PluginConfigurationScope.Route, $"route:{section.Key}/{route.Key}")));
            }
            sites.Add(new(section.Key, names, plugins, routes.ToArray()));
        }
        return new(global, sites.ToArray());
    }

    private Dictionary<string, Values> MergePlugins(Dictionary<string, Values> parent, IConfigurationSection section,
        PluginConfigurationScope scope, string source)
    {
        var result = new Dictionary<string, Values>(parent, StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in section.GetChildren())
        {
            if (!parent.TryGetValue(plugin.Key, out var value))
                throw new InvalidOperationException($"{plugin.Path}: plugin is unavailable or does not support scoped configuration.");
            result[plugin.Key] = Merge(value, plugin, scope, source);
        }
        return result;
    }

    private static Values Merge(Values parent, IConfigurationSection section, PluginConfigurationScope scope, string source)
    {
        var fields = new Dictionary<string, string>(parent.Fields, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(parent.Sources, StringComparer.OrdinalIgnoreCase);
        Apply(section, parent.Type, "");
        var errors = PluginConfigurationSchema.Validate(parent.Type, fields);
        if (errors.Count > 0) throw new InvalidOperationException($"{section.Path}: invalid configuration fields: {string.Join(", ", errors.Keys)}.");
        return new(parent.Type, fields, sources);

        void Apply(IConfigurationSection node, Type type, string prefix)
        {
            foreach (var child in node.GetChildren())
            {
                var property = type.GetProperties().FirstOrDefault(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true &&
                    Key(p).Equals(child.Key, StringComparison.OrdinalIgnoreCase));
                if (property == null) throw new InvalidOperationException($"{child.Path}: unknown field.");
                var attribute = property.GetCustomAttribute<PluginConfigurationAttribute>();
                if (((attribute?.Scopes ?? PluginConfigurationScope.All) & scope) == 0 ||
                    (scope != PluginConfigurationScope.Global && attribute?.RequiresRestart == true))
                    throw new InvalidOperationException($"{child.Path}: field is not permitted in {scope} scope.");
                var path = prefix + Key(property);
                var collection = typeof(IEnumerable).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string);
                if (!collection && child.GetChildren().Any()) { Apply(child, property.PropertyType, path + ":"); continue; }
                // Collections replace as a unit; never append inherited elements.
                foreach (var key in fields.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
                { fields.Remove(key); sources.Remove(key); }
                var entries = child.AsEnumerable().Where(p => p.Value != null).ToArray();
                if (entries.Length == 0) { fields[path] = ""; sources[path] = source; }
                foreach (var pair in entries)
                {
                    var key = path + pair.Key[child.Path.Length..];
                    foreach (var field in PluginConfigurationSchema.Describe(parent.Type))
                    {
                        var pattern = field.Path.Split(':');
                        var segments = key.Split(':');
                        if (pattern.Length <= segments.Length && pattern.Select((p, i) => p == "*" || p.Equals(segments[i], StringComparison.OrdinalIgnoreCase)).All(p => p) &&
                            ((field.Scopes & scope) == 0 || (scope != PluginConfigurationScope.Global && field.RequiresRestart)))
                            throw new InvalidOperationException($"{child.Path}: field is not permitted in {scope} scope.");
                    }
                    fields[key] = pair.Value!;
                    sources[key] = source;
                }
            }
        }
    }

    private void Reload()
    {
        lock (_reloadLock)
        {
            try
            {
                var next = Build();
                foreach (var (id, previous) in _snapshot.Global)
                {
                    if (!next.Global.TryGetValue(id, out var current)) continue;
                    foreach (var field in PluginConfigurationSchema.Describe(previous.Type).Where(f => f.RequiresRestart))
                    {
                        var prefix = field.Path.Split(':');
                        bool Includes(string key)
                        {
                            var parts = key.Split(':');
                            return parts.Length >= prefix.Length && prefix.Select((p, i) => p == "*" || p.Equals(parts[i], StringComparison.OrdinalIgnoreCase)).All(p => p);
                        }
                        if (!previous.Fields.Where(p => Includes(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                            .SequenceEqual(current.Fields.Where(p => Includes(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)))
                            throw new InvalidOperationException($"Plugins:{id}:{field.Path}: changes require restart.");
                    }
                }
                Volatile.Write(ref _snapshot, next); LastReloadError = null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { LastReloadError = ex.Message; _logger.LogWarning("Plugin configuration reload rejected: {Reason}", ex.Message); }
        }
    }

    private static void CheckKeys(IConfigurationSection section, params string[] allowed)
    {
        foreach (var child in section.GetChildren())
            if (!allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{child.Path}: unknown directive.");
    }
    private static bool ValidHost(string host) => Uri.CheckHostName(host.StartsWith("*.") ? host[2..] : host) != UriHostNameType.Unknown;
    private static int HostScore(string pattern, string host) => pattern.Equals(host, StringComparison.OrdinalIgnoreCase) ? 10000 + pattern.Length :
        pattern.StartsWith("*.") && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1 ? pattern.Length : -1;
    private static bool PathMatches(string pattern, string path) => pattern.EndsWith("/**")
        ? path.Equals(pattern[..^3], StringComparison.Ordinal) || path.StartsWith(pattern[..^2], StringComparison.Ordinal)
        : path.Equals(pattern, StringComparison.Ordinal);
    private static string Key(PropertyInfo property) => property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? property.Name;

    private static void FlattenDefaults(object value, string prefix, Dictionary<string, string> result)
    {
        foreach (var property in value.GetType().GetProperties().Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0))
        {
            var child = property.GetValue(value);
            if (child == null) continue;
            var path = prefix + Key(property);
            if (child is string || System.ComponentModel.TypeDescriptor.GetConverter(property.PropertyType).CanConvertFrom(typeof(string)))
                result[path] = Convert.ToString(child, CultureInfo.InvariantCulture)!;
            else if (child is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary) Write(entry.Value, path + ":" + entry.Key);
                if (dictionary.Count == 0) result[path] = "";
            }
            else if (child is IEnumerable sequence)
            {
                var index = 0;
                foreach (var item in sequence)
                {
                    if (item is DictionaryEntry entry) Write(entry.Value, path + ":" + entry.Key);
                    else Write(item, path + ":" + index++);
                }
                if (!result.Keys.Any(k => k.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase))) result[path] = "";
            }
            else FlattenDefaults(child, path + ":", result);
        }
        void Write(object? item, string path)
        {
            if (item == null) return;
            if (System.ComponentModel.TypeDescriptor.GetConverter(item.GetType()).CanConvertFrom(typeof(string))) result[path] = Convert.ToString(item, CultureInfo.InvariantCulture)!;
            else FlattenDefaults(item, path + ":", result);
        }
    }
    public void Dispose() => _subscription.Dispose();
}
