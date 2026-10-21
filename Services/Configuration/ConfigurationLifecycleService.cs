using System.Text.RegularExpressions;
using LyWaf.Services.WafRule;

namespace LyWaf.Services.Configuration;

/// <summary>
/// 描述一次配置重载涉及的字段和实际生效级别。
/// </summary>
public sealed record ConfigurationChangeImpact(
    IReadOnlyList<string> Immediate,
    IReadOnlyList<string> RouteRebuild,
    IReadOnlyList<string> RestartRequired)
{
    /// <summary>是否包含必须重启进程才能生效的字段。</summary>
    public bool RequiresRestart => RestartRequired.Count > 0;
}

/// <summary>
/// 对配置快照进行变更分级，并在重载后执行路由和安全策略冒烟检查。
/// </summary>
public sealed class ConfigurationLifecycleService(ConfigurationReloadRecovery? recovery = null)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IConfigurationRoot, object> ReloadGates = new();
    private static readonly string[] RestartPrefixes =
        ["WafInfos:Listens", "Plugins:Directories", "OpenTelemetry", "Metrics", "DuckDb"];
    private static readonly string[] RoutePrefixes =
        ["ReverseProxy", "FileServer", "SimpleRes", "ABTest"];

    /// <summary>捕获可用于重载前后比较的扁平配置快照。</summary>
    public IReadOnlyDictionary<string, string?> Capture(IConfiguration configuration)
    {
        var values = configuration.AsEnumerable().Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return configuration is IConfigurationRoot root ? new ActiveSnapshot(values,
            root.Providers.ToDictionary(provider => provider, CaptureProvider)) : values;
    }

    /// <summary>重载配置、执行冒烟检查并返回字段生效级别。</summary>
    public ConfigurationChangeImpact ReloadAndValidate(
        IConfigurationRoot root,
        IReadOnlyDictionary<string, string?> before)
    {
        lock (ReloadGates.GetValue(root, _ => new object()))
        {
            try
            {
                root.Reload();
                Validate(root);
                return Analyze(before, Capture(root));
            }
            catch (Exception reloadFailure)
            {
                try
                {
                    RestoreActiveSnapshot(root, before);
                    Validate(root);
                    recovery?.NotifyRestored();
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException("Configuration reload and active-state recovery failed", reloadFailure, restoreFailure);
                }
                throw;
            }
        }
    }

    private static void RestoreActiveSnapshot(
        IConfigurationRoot root,
        IReadOnlyDictionary<string, string?> snapshot)
    {
        if (snapshot is ActiveSnapshot active)
        {
            foreach (var (provider, previous) in active.Providers)
            {
                if (provider is IRestorableConfigurationProvider restorable)
                {
                    restorable.RestoreValues(previous);
                    continue;
                }
                // Standard YAML/JSON providers expose a stream parser that replaces their data without
                // rereading the rejected file or publishing a partially restored change notification.
                if (provider is FileConfigurationProvider file && Path.GetExtension(file.Source.Path ?? "").ToLowerInvariant()
                    is ".yaml" or ".yml" or ".json")
                {
                    using var stream = new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(previous));
                    file.Load(stream);
                    continue;
                }
                var current = CaptureProvider(provider);
                foreach (var key in current.Keys.Concat(previous.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!current.TryGetValue(key, out var value) || !previous.TryGetValue(key, out var old) || value != old)
                        provider.Set(key, previous.GetValueOrDefault(key));
            }
            return;
        }
        var currentKeys = root.AsEnumerable().Select(static item => item.Key);
        foreach (var key in currentKeys.Concat(snapshot.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            root[key] = snapshot.GetValueOrDefault(key);
    }

    private static Dictionary<string, string?> CaptureProvider(IConfigurationProvider provider)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var parents = new Stack<string?>();
        parents.Push(null);
        while (parents.TryPop(out var parent))
            foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = parent is null ? child : parent + ":" + child;
                if (provider.TryGet(key, out var value)) values[key] = value;
                parents.Push(key);
            }
        return values;
    }

    private sealed class ActiveSnapshot(Dictionary<string, string?> values,
        Dictionary<IConfigurationProvider, Dictionary<string, string?>> providers)
        : Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase)
    {
        public Dictionary<IConfigurationProvider, Dictionary<string, string?>> Providers { get; } = providers;
    }

    /// <summary>验证当前配置中会影响路由选择和安全匹配的关键约束。</summary>
    public void Validate(IConfiguration configuration)
    {
        ValidateListens(configuration);
        ValidateRoutes(configuration);
        ValidateWafRules(configuration);
    }

    private static ConfigurationChangeImpact Analyze(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after)
    {
        var changed = before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => !before.TryGetValue(key, out var oldValue) ||
                          !after.TryGetValue(key, out var newValue) || oldValue != newValue)
            .Select(TopLevelChange).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        return new(
            changed.Where(x => !HasPrefix(x, RestartPrefixes) && !HasPrefix(x, RoutePrefixes)).ToArray(),
            changed.Where(x => HasPrefix(x, RoutePrefixes)).ToArray(),
            changed.Where(x => HasPrefix(x, RestartPrefixes)).ToArray());
    }

    private static string TopLevelChange(string key)
    {
        if (key.StartsWith("WafInfos:Listens", StringComparison.OrdinalIgnoreCase)) return "WafInfos:Listens";
        if (key.StartsWith("Plugins:Directories", StringComparison.OrdinalIgnoreCase)) return "Plugins:Directories";
        return key.Split(':')[0];
    }

    private static bool HasPrefix(string key, IEnumerable<string> prefixes)
        => prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static void ValidateListens(IConfiguration configuration)
    {
        foreach (var listen in configuration.GetSection("WafInfos:Listens").GetChildren())
        {
            if (!int.TryParse(listen["Port"], out var port) || port is < 1 or > 65535)
                throw new InvalidDataException($"监听端口无效: {listen["Port"]}");
        }
    }

    private static void ValidateRoutes(IConfiguration configuration)
    {
        var clusters = configuration.GetSection("ReverseProxy:Clusters").GetChildren()
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in configuration.GetSection("ReverseProxy:Routes").GetChildren())
        {
            if (!routeIds.Add(route.Key)) throw new InvalidDataException($"路由 ID 重复: {route.Key}");
            var clusterId = route["ClusterId"];
            if (!string.IsNullOrWhiteSpace(clusterId) && !clusters.Contains(clusterId))
                throw new InvalidDataException($"路由 {route.Key} 引用了不存在的集群 {clusterId}");
        }
    }

    private static void ValidateWafRules(IConfiguration configuration)
    {
        foreach (var rule in configuration.GetSection("Protect:WafRules").GetChildren())
        {
            ValidateCondition(rule.Key, rule["Field"], rule["Operator"], rule["Value"]);
            foreach (var condition in rule.GetSection("Conditions").GetChildren())
                ValidateCondition(rule.Key, condition["Field"], condition["Operator"], condition["Value"]);
        }
    }

    private static void ValidateCondition(string ruleId, string? field, string? operation, string? value)
    {
        if (!string.IsNullOrWhiteSpace(field) && !Enum.TryParse<WafMatchField>(field, true, out _))
            throw new InvalidDataException($"WAF 规则 {ruleId} 的字段无效: {field}");
        if (!string.IsNullOrWhiteSpace(operation) && !Enum.TryParse<WafMatchOperator>(operation, true, out var parsed))
            throw new InvalidDataException($"WAF 规则 {ruleId} 的操作符无效: {operation}");
        if (Enum.TryParse<WafMatchOperator>(operation, true, out parsed) && parsed == WafMatchOperator.Regex)
        {
            try { _ = new Regex(value ?? "", RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { throw new InvalidDataException($"WAF 规则 {ruleId} 的正则表达式无效", ex); }
        }
    }
}
