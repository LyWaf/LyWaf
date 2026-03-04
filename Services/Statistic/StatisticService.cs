
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.RateLimiting;
using LyWaf.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;


public interface IStatisticService
{
    public Task<string> GetMatchPath(string path);

    public bool IsWhitePath(string path);

    public StatisticOptions GetOption();

    /// <summary>
    /// 获取 CC 限制规则列表
    /// </summary>
    List<LimitCcOption> GetLimitCcRules();

    /// <summary>
    /// 添加 CC 限制规则
    /// </summary>
    bool AddLimitCcRule(LimitCcOption rule);

    /// <summary>
    /// 移除 CC 限制规则
    /// </summary>
    bool RemoveLimitCcRule(string path);
    
    // =============== 高级 CC 规则管理 ===============
    
    /// <summary>
    /// 获取所有高级 CC 规则
    /// </summary>
    List<AdvancedCcRule> GetAdvancedCcRules();
    
    /// <summary>
    /// 获取指定类型的高级 CC 规则
    /// </summary>
    List<AdvancedCcRule> GetAdvancedCcRulesByType(CcRuleType type);
    
    /// <summary>
    /// 获取单个高级 CC 规则
    /// </summary>
    AdvancedCcRule? GetAdvancedCcRule(string ruleId);
    
    /// <summary>
    /// 添加高级 CC 规则
    /// </summary>
    bool AddAdvancedCcRule(AdvancedCcRule rule);
    
    /// <summary>
    /// 更新高级 CC 规则
    /// </summary>
    bool UpdateAdvancedCcRule(AdvancedCcRule rule);
    
    /// <summary>
    /// 删除高级 CC 规则
    /// </summary>
    bool RemoveAdvancedCcRule(string ruleId);
    
    /// <summary>
    /// 启用/禁用高级 CC 规则
    /// </summary>
    bool ToggleAdvancedCcRule(string ruleId, bool enabled);

    // =============== 白名单路径 / 路径统计规则管理 ===============

    /// <summary>获取合并后的白名单路径列表</summary>
    List<string> GetWhitePaths();
    /// <summary>添加白名单路径</summary>
    bool AddWhitePath(string path);
    /// <summary>移除白名单路径</summary>
    bool RemoveWhitePath(string path);

    /// <summary>获取合并后的路径统计规则列表</summary>
    List<string> GetPathStas();
    /// <summary>添加路径统计规则</summary>
    bool AddPathSta(string path);
    /// <summary>移除路径统计规则</summary>
    bool RemovePathSta(string path);
}


public class StatisticService : IStatisticService
{
    private StatisticOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StatisticService> _logger;

    private Dictionary<string, string> _isInPathStas = [];

    private Dictionary<string, int> _isHasNextPathStas = [];

    // 动态添加的 CC 规则
    private readonly List<LimitCcOption> _dynamicLimitCc = [];
    private readonly object _ccRuleLock = new();
    
    // 动态添加的高级 CC 规则
    private readonly List<AdvancedCcRule> _dynamicAdvancedCcRules = [];
    private readonly object _advancedCcRuleLock = new();

    // 缓存已排序的高级 CC 规则列表
    private List<AdvancedCcRule> _cachedAdvancedCcRules = [];
    // 按类型分组的缓存字典
    private Dictionary<CcRuleType, List<AdvancedCcRule>> _cachedAdvancedCcRulesByType = new();
    // 已排除的配置规则 ID（用于"删除"配置中的规则）
    private readonly HashSet<string> _excludedCcRuleIds = new();

    // 动态白名单路径 / 路径统计规则
    private readonly HashSet<string> _dynamicWhitePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedWhitePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicPathStas = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedPathStas = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pathLock = new();

    private static readonly string StatisticStateFilePath =
        Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.statistic.json");

    private const int HAS_ANY = 0x00000001;
    private const int HAS_MATCH = 0x00000002;
    private const int HAS_FULL = 0x00000004;

    private const int HAS_ALL = HAS_ANY | HAS_MATCH | HAS_FULL;

    public StatisticService(
        IOptionsMonitor<StatisticOptions> options,
        IServiceProvider _serviceProvider, IConfiguration configuration, IMemoryCache cache,
        ILogger<StatisticService> logger)
    {
        _options = options.CurrentValue;
        _cache = cache;
        _logger = logger;

        // 加载持久化状态
        LoadStatisticState();

        // 可以订阅变更，但需注意生命周期和内存泄漏
        options.OnChange(newConfig =>
        {
            _options = newConfig;
            BuildStatistic();
            lock (_advancedCcRuleLock)
            {
                RebuildAdvancedCcRulesCache();
            }
        });
        BuildStatistic();
        RebuildAdvancedCcRulesCache();
    }

    private static int ConvertStrToSta(string str)
    {
        if (str == "*")
        {
            return HAS_ANY;
        }
        else if (str.StartsWith('{') && str.EndsWith('}'))
        {
            return HAS_MATCH;
        }
        else
        {
            return HAS_FULL;
        }
    }

    /// <summary>构建合并后的有效白名单路径集合</summary>
    private HashSet<string> BuildEffectiveWhitePaths()
    {
        var result = new HashSet<string>(_options.WhitePaths, StringComparer.OrdinalIgnoreCase);
        lock (_pathLock)
        {
            result.UnionWith(_dynamicWhitePaths);
            result.ExceptWith(_removedWhitePaths);
        }
        return result;
    }

    /// <summary>构建合并后的有效路径统计规则集合</summary>
    private HashSet<string> BuildEffectivePathStas()
    {
        var result = new HashSet<string>(_options.PathStas, StringComparer.OrdinalIgnoreCase);
        lock (_pathLock)
        {
            result.UnionWith(_dynamicPathStas);
            result.ExceptWith(_removedPathStas);
        }
        return result;
    }

    private void BuildStatistic()
    {
        var newPathStatistic = new Dictionary<string, string>();
        var newNextPathStatistic = new Dictionary<string, int>{
            {"", HAS_MATCH | HAS_FULL}
        };

        // 合并配置中的 CC 规则和动态添加的 CC 规则
        var allLimitCc = new List<LimitCcOption>(_options.LimitCc);
        lock (_ccRuleLock)
        {
            allLimitCc.AddRange(_dynamicLimitCc);
        }

        // 使用合并后的有效集合
        var effectivePathStas = BuildEffectivePathStas();
        var effectiveWhitePaths = BuildEffectiveWhitePaths();

        foreach (var limit in allLimitCc)
        {
            if (limit.Path.Length > 0)
            {
                effectivePathStas.Add(limit.Path);
            }
        }

        foreach (var path in effectiveWhitePaths)
        {
            if (path.Length > 0)
            {
                effectivePathStas.Add(path);
            }
        }

        foreach (var path in effectivePathStas)
        {
            var l = path.Trim('/').Split('/');
            var buildList = new List<string>();
            for (int i = 0; i < l.Length; i++)
            {
                var str = l[i];
                var val = ConvertStrToSta(str);
                if ((val & HAS_ANY) != 0)
                {
                    buildList.Add(StatisticOptions.ANY);
                }
                else if ((val & HAS_MATCH) != 0)
                {
                    buildList.Add(StatisticOptions.MATCH);
                }
                else
                {
                    buildList.Add(str);
                }
                if (i == l.Length - 1)
                {
                    newPathStatistic.Add(string.Join(StatisticOptions.CONNECT, buildList), path);
                }
                else
                {
                    var c = string.Join(StatisticOptions.CONNECT, buildList);
                    val = ConvertStrToSta(l[i + 1]);
                    if (newNextPathStatistic.TryGetValue(c, out var v))
                    {
                        newNextPathStatistic[c] = v | val;
                    }
                    else
                    {
                        newNextPathStatistic[c] = val;
                    }
                }
            }
        }
        _isInPathStas = newPathStatistic;
        _isHasNextPathStas = newNextPathStatistic;
    }

    // false未匹配, true已匹配, 如果已匹配且返回有值则直接return
    private bool CheckVaild(LinkedList<string> nowList, out string? path)
    {
        var c = string.Join(StatisticOptions.CONNECT, nowList);
        if (_isInPathStas.TryGetValue(c, out string? val))
        {
            path = val;
            return true;
        }
        path = null;
        return false;
    }

    public async Task<string> GetMatchPath(string path)
    {
        var cacheKey = string.Format("MathPath:{0}", path);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(120);

            var l = path.Trim('/').Split('/');
            var buildList = new List<LinkedList<string>>
            {
                new([])
            };

            for (int i = 0; i < l.Length; i++)
            {
                var str = l[i];
                var newValidList = new List<LinkedList<string>>();
                foreach (var nowList in buildList)
                {
                    if (!_isHasNextPathStas.TryGetValue(string.Join(StatisticOptions.CONNECT, nowList), out var index))
                    {
                        continue;
                    }
                    if ((index & HAS_ANY) != 0)
                    {
                        nowList.AddLast(StatisticOptions.ANY);
                        if (CheckVaild(nowList, out var ret))
                        {
                            return ret!;
                        }
                        throw new Exception("不可能进入!");
                    }
                    if ((index & HAS_FULL) != 0)
                    {
                        var operList = nowList;
                        // 如果不存在后续则不需要拷贝
                        if ((index & HAS_MATCH) != 0)
                        {
                            operList = new LinkedList<string>(nowList);
                        }
                        operList.AddLast(str);
                        if (i == l.Length - 1 && CheckVaild(operList, out var ret))
                        {
                            return ret!;
                        }
                        else
                        {
                            newValidList.Add(operList);
                        }

                    }

                    if ((index & HAS_MATCH) != 0)
                    {
                        nowList.AddLast(StatisticOptions.MATCH);
                        if (i == l.Length - 1 && CheckVaild(nowList, out var ret))
                        {
                            return ret!;
                        }
                        else
                        {
                            newValidList.Add(nowList);
                        }
                    }
                }
                if (newValidList.Count == 0)
                {
                    return path;
                }
                buildList = newValidList;
            }
            return path;
        }) ?? path;

    }

    public bool IsWhitePath(string path)
    {
        if (_options.WhitePaths.Contains(path))
        {
            lock (_pathLock)
            {
                return !_removedWhitePaths.Contains(path);
            }
        }
        lock (_pathLock)
        {
            return _dynamicWhitePaths.Contains(path);
        }
    }

    public StatisticOptions GetOption()
    {
        return _options;
    }

    /// <summary>
    /// 获取 CC 限制规则列表
    /// </summary>
    public List<LimitCcOption> GetLimitCcRules()
    {
        lock (_ccRuleLock)
        {
            var allRules = new List<LimitCcOption>(_options.LimitCc);
            allRules.AddRange(_dynamicLimitCc);
            return allRules;
        }
    }

    /// <summary>
    /// 添加 CC 限制规则
    /// </summary>
    public bool AddLimitCcRule(LimitCcOption rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Path))
            return false;

        lock (_ccRuleLock)
        {
            // 检查是否已存在相同路径的规则
            if (_dynamicLimitCc.Any(r => r.Path.Equals(rule.Path, StringComparison.OrdinalIgnoreCase)))
                return false;

            _dynamicLimitCc.Add(rule);
            BuildStatistic();
            _logger.LogInformation("已添加动态 CC 规则: Path={Path}, Period={Period}, LimitNum={LimitNum}", 
                rule.Path, rule.Period, rule.LimitNum);
            return true;
        }
    }

    /// <summary>
    /// 移除 CC 限制规则
    /// </summary>
    public bool RemoveLimitCcRule(string path)
    {
        lock (_ccRuleLock)
        {
            var rule = _dynamicLimitCc.FirstOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
            {
                _dynamicLimitCc.Remove(rule);
                BuildStatistic();
                _logger.LogInformation("已移除动态 CC 规则: Path={Path}", path);
                return true;
            }
        }
        return false;
    }
    
    // =============== 高级 CC 规则管理 ===============

    /// <summary>
    /// 重建高级 CC 规则缓存（调用前需持有 _advancedCcRuleLock）
    /// </summary>
    private void RebuildAdvancedCcRulesCache()
    {
        var allRules = new List<AdvancedCcRule>();
        // 合并配置规则，过滤已排除的
        allRules.AddRange(_options.AdvancedCcRules.Where(r => !_excludedCcRuleIds.Contains(r.Id)));
        allRules.AddRange(_options.CcRules.Where(r => !_excludedCcRuleIds.Contains(r.Id)));
        allRules.AddRange(_dynamicAdvancedCcRules);
        var sorted = allRules.OrderBy(r => r.Priority).ThenBy(r => r.CreatedAt).ToList();
        _cachedAdvancedCcRules = sorted;
        // 按类型分组缓存
        _cachedAdvancedCcRulesByType = sorted.GroupBy(r => r.Type)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// 获取所有高级 CC 规则
    /// </summary>
    public List<AdvancedCcRule> GetAdvancedCcRules()
    {
        return _cachedAdvancedCcRules;
    }
    
    /// <summary>
    /// 获取指定类型的高级 CC 规则
    /// </summary>
    public List<AdvancedCcRule> GetAdvancedCcRulesByType(CcRuleType type)
    {
        return _cachedAdvancedCcRulesByType.TryGetValue(type, out var rules) ? rules : [];
    }
    
    /// <summary>
    /// 获取单个高级 CC 规则
    /// </summary>
    public AdvancedCcRule? GetAdvancedCcRule(string ruleId)
    {
        lock (_advancedCcRuleLock)
        {
            return _options.AdvancedCcRules.FirstOrDefault(r => r.Id == ruleId)
                ?? _options.CcRules.FirstOrDefault(r => r.Id == ruleId)
                ?? _dynamicAdvancedCcRules.FirstOrDefault(r => r.Id == ruleId);
        }
    }
    
    /// <summary>
    /// 添加高级 CC 规则
    /// </summary>
    public bool AddAdvancedCcRule(AdvancedCcRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
            return false;
            
        lock (_advancedCcRuleLock)
        {
            // 检查是否已存在相同 ID 的规则
            if (_dynamicAdvancedCcRules.Any(r => r.Id == rule.Id) ||
                _options.AdvancedCcRules.Any(r => r.Id == rule.Id) ||
                _options.CcRules.Any(r => r.Id == rule.Id))
                return false;
            
            // 确保有唯一 ID
            if (string.IsNullOrEmpty(rule.Id))
                rule.Id = Guid.NewGuid().ToString("N")[..8];
                
            rule.CreatedAt = DateTime.UtcNow;
            _dynamicAdvancedCcRules.Add(rule);
            RebuildAdvancedCcRulesCache();

            _logger.LogInformation("已添加高级 CC 规则: Id={Id}, Name={Name}, Type={Type}",
                rule.Id, rule.Name, rule.Type);
            return true;
        }
    }
    
    /// <summary>
    /// 更新高级 CC 规则
    /// </summary>
    public bool UpdateAdvancedCcRule(AdvancedCcRule rule)
    {
        lock (_advancedCcRuleLock)
        {
            // 先在动态规则中查找
            var existingIndex = _dynamicAdvancedCcRules.FindIndex(r => r.Id == rule.Id);
            if (existingIndex >= 0)
            {
                // 保留创建时间
                rule.CreatedAt = _dynamicAdvancedCcRules[existingIndex].CreatedAt;
                _dynamicAdvancedCcRules[existingIndex] = rule;
                RebuildAdvancedCcRulesCache();
                _logger.LogInformation("已更新高级 CC 规则: Id={Id}, Name={Name}", rule.Id, rule.Name);
                return true;
            }

            // 如果在配置规则中存在，则添加到动态规则（覆盖）
            if (_options.AdvancedCcRules.Any(r => r.Id == rule.Id) ||
                _options.CcRules.Any(r => r.Id == rule.Id))
            {
                _dynamicAdvancedCcRules.Add(rule);
                RebuildAdvancedCcRulesCache();
                _logger.LogInformation("已覆盖配置中的高级 CC 规则: Id={Id}, Name={Name}", rule.Id, rule.Name);
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// 删除高级 CC 规则
    /// </summary>
    public bool RemoveAdvancedCcRule(string ruleId)
    {
        lock (_advancedCcRuleLock)
        {
            var removed = false;

            // 尝试从动态规则中移除
            var rule = _dynamicAdvancedCcRules.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null)
            {
                _dynamicAdvancedCcRules.Remove(rule);
                removed = true;
            }

            // 如果是配置中的规则，添加到排除列表
            if (_options.AdvancedCcRules.Any(r => r.Id == ruleId) ||
                _options.CcRules.Any(r => r.Id == ruleId))
            {
                if (_excludedCcRuleIds.Add(ruleId))
                {
                    removed = true;
                }
            }

            if (removed)
            {
                RebuildAdvancedCcRulesCache();
                _logger.LogInformation("已删除高级 CC 规则: Id={Id}", ruleId);
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// 启用/禁用高级 CC 规则
    /// </summary>
    public bool ToggleAdvancedCcRule(string ruleId, bool enabled)
    {
        lock (_advancedCcRuleLock)
        {
            // 在动态规则中查找
            var rule = _dynamicAdvancedCcRules.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null)
            {
                rule.Enabled = enabled;
                RebuildAdvancedCcRulesCache();
                _logger.LogInformation("高级 CC 规则状态已更改: Id={Id}, Enabled={Enabled}", ruleId, enabled);
                return true;
            }

            // 在配置规则中查找，需要复制到动态规则中修改
            var configRule = _options.AdvancedCcRules.FirstOrDefault(r => r.Id == ruleId)
                ?? _options.CcRules.FirstOrDefault(r => r.Id == ruleId);
            if (configRule != null)
            {
                // 创建一个副本并修改
                var newRule = new AdvancedCcRule
                {
                    Id = configRule.Id,
                    Name = configRule.Name,
                    Enabled = enabled,
                    Type = configRule.Type,
                    Conditions = configRule.Conditions,
                    Period = configRule.Period,
                    Threshold = configRule.Threshold,
                    Action = configRule.Action,
                    ActionSeconds = configRule.ActionSeconds,
                    Priority = configRule.Priority,
                    CreatedAt = configRule.CreatedAt
                };
                _dynamicAdvancedCcRules.Add(newRule);
                RebuildAdvancedCcRulesCache();
                _logger.LogInformation("高级 CC 规则状态已更改（从配置复制）: Id={Id}, Enabled={Enabled}", ruleId, enabled);
                return true;
            }
        }
        return false;
    }

    // =============== 白名单路径管理 ===============

    public List<string> GetWhitePaths()
    {
        return BuildEffectiveWhitePaths().OrderBy(p => p).ToList();
    }

    public bool AddWhitePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();

        lock (_pathLock)
        {
            // 已在配置中且未被移除 → 已存在
            if (_options.WhitePaths.Contains(path) && !_removedWhitePaths.Contains(path))
                return false;
            // 已在动态列表中 → 已存在
            if (_dynamicWhitePaths.Contains(path))
                return false;

            // 如果之前从配置中移除过，恢复它
            _removedWhitePaths.Remove(path);
            // 如果不在配置中，添加到动态列表
            if (!_options.WhitePaths.Contains(path))
                _dynamicWhitePaths.Add(path);
        }

        BuildStatistic();
        SaveStatisticState();
        _logger.LogInformation("已添加白名单路径: {Path}", path);
        return true;
    }

    public bool RemoveWhitePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();

        lock (_pathLock)
        {
            var removed = _dynamicWhitePaths.Remove(path);
            if (_options.WhitePaths.Contains(path))
            {
                removed = _removedWhitePaths.Add(path) || removed;
            }
            if (!removed) return false;
        }

        BuildStatistic();
        SaveStatisticState();
        _logger.LogInformation("已移除白名单路径: {Path}", path);
        return true;
    }

    // =============== 路径统计规则管理 ===============

    public List<string> GetPathStas()
    {
        return BuildEffectivePathStas().OrderBy(p => p).ToList();
    }

    public bool AddPathSta(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();

        lock (_pathLock)
        {
            if (_options.PathStas.Contains(path) && !_removedPathStas.Contains(path))
                return false;
            if (_dynamicPathStas.Contains(path))
                return false;

            _removedPathStas.Remove(path);
            if (!_options.PathStas.Contains(path))
                _dynamicPathStas.Add(path);
        }

        BuildStatistic();
        SaveStatisticState();
        _logger.LogInformation("已添加路径统计规则: {Path}", path);
        return true;
    }

    public bool RemovePathSta(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();

        lock (_pathLock)
        {
            var removed = _dynamicPathStas.Remove(path);
            if (_options.PathStas.Contains(path))
            {
                removed = _removedPathStas.Add(path) || removed;
            }
            if (!removed) return false;
        }

        BuildStatistic();
        SaveStatisticState();
        _logger.LogInformation("已移除路径统计规则: {Path}", path);
        return true;
    }

    // =============== 持久化 ===============

    private void LoadStatisticState()
    {
        try
        {
            if (!File.Exists(StatisticStateFilePath)) return;

            var json = File.ReadAllText(StatisticStateFilePath);
            var state = JsonSerializer.Deserialize<StatisticState>(json);
            if (state == null) return;

            lock (_pathLock)
            {
                foreach (var p in state.AddedWhitePaths ?? []) _dynamicWhitePaths.Add(p);
                foreach (var p in state.RemovedWhitePaths ?? []) _removedWhitePaths.Add(p);
                foreach (var p in state.AddedPathStas ?? []) _dynamicPathStas.Add(p);
                foreach (var p in state.RemovedPathStas ?? []) _removedPathStas.Add(p);
            }

            _logger.LogInformation("已加载统计配置状态: {Path}", StatisticStateFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载统计配置状态失败: {Message}", ex.Message);
        }
    }

    private void SaveStatisticState()
    {
        try
        {
            StatisticState state;
            lock (_pathLock)
            {
                state = new StatisticState
                {
                    AddedWhitePaths = [.. _dynamicWhitePaths],
                    RemovedWhitePaths = [.. _removedWhitePaths],
                    AddedPathStas = [.. _dynamicPathStas],
                    RemovedPathStas = [.. _removedPathStas],
                };
            }

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StatisticStateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存统计配置状态失败: {Message}", ex.Message);
        }
    }

    private class StatisticState
    {
        public List<string> AddedWhitePaths { get; set; } = [];
        public List<string> RemovedWhitePaths { get; set; } = [];
        public List<string> AddedPathStas { get; set; } = [];
        public List<string> RemovedPathStas { get; set; } = [];
    }
}