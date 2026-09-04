using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLog;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using LyWaf.Services.Telemetry;
using LyWaf.Plugins.Packaging;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件配置选项
/// </summary>
public class PluginOptions
{
    /// <summary>
    /// 是否启用插件系统
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 插件目录（相对于应用程序目录）
    /// </summary>
    public string PluginDirectory { get; set; } = "plugins";

    /// <summary>
    /// 插件数据目录
    /// </summary>
    public string DataDirectory { get; set; } = "plugin_data";

    /// <summary>
    /// 禁用的插件列表
    /// </summary>
    public HashSet<string> DisabledPlugins { get; set; } = [];

    /// <summary>
    /// 是否启用热重载
    /// </summary>
    public bool EnableHotReload { get; set; } = false;

    /// <summary>单个插件生命周期操作允许占用的最长秒数。</summary>
    public int LifecycleTimeoutSeconds { get; set; } = 30;

    /// <summary>插件包允许的最大字节数。</summary>
    public long MaxPackageBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>插件包解压后的最大总字节数。</summary>
    public long MaxExtractedPackageBytes { get; set; } = 250 * 1024 * 1024;

    /// <summary>插件包允许包含的最大已校验文件数。</summary>
    public int MaxPackageFiles { get; set; } = 512;

    /// <summary>是否拒绝没有可信 RSA 签名的插件包。</summary>
    public bool RequireSignedPackages { get; set; }

    /// <summary>签名密钥 ID 到 PEM 公钥的映射。</summary>
    public Dictionary<string, string> TrustedSigningKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 各插件的配置
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> PluginConfigs { get; set; } = [];

    /// <summary>外部插件逐项获准的能力；未配置时默认不授予特权。</summary>
    public Dictionary<string, PluginPermission> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 系统插件 ID 列表
    /// 系统插件是 LyWaf 自带的插件，默认加载，不会被外部插件覆盖
    /// 除非在 DisabledPlugins 中明确禁用
    /// </summary>
    public HashSet<string> SystemPlugins { get; } = ["request-logger", "analysis", "web-tamper"];

    /// <summary>统一插件代理 API 的资源限额。</summary>
    public PluginApiProxyOptions ApiProxy { get; set; } = new();
}

/// <summary>插件代理 API 的请求体、速率和并发限制。</summary>
public sealed class PluginApiProxyOptions
{
    /// <summary>单次请求体最大字节数。</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    /// <summary>同一登录用户调用同一插件的每分钟最大请求数。</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>所有插件代理请求共享的最大并发数。</summary>
    public int MaxConcurrentRequests { get; set; } = 16;
}

/// <summary>
/// 插件信息
/// </summary>
public record PluginInfo
{
    public required ILyWafPlugin Plugin { get; init; }
    public required IPluginContext Context { get; init; }
    public AssemblyLoadContext? LoadContext { get; init; }
    public string? AssemblyPath { get; init; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 插件管理器实现
/// </summary>
public class PluginManager : IPluginManager
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<string, PluginInfo> _plugins = new();
    private PluginOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IPluginEventBus _eventBus;
    private IServiceProvider? _serviceProvider;

    public PluginManager(IConfiguration configuration, IPluginEventBus eventBus, IOptionsMonitor<PluginOptions> optionsMonitor)
    {
        _configuration = configuration;
        _eventBus = eventBus;
        _options = optionsMonitor.CurrentValue;

        optionsMonitor.OnChange(newOptions =>
        {
            _options = newOptions;
            _logger.Info("插件配置已动态更新");
            _ = Task.Run(() => OnOptionsChangedAsync(newOptions));
        });
    }

    /// <summary>
    /// 配置变更时动态更新插件启用/禁用状态
    /// </summary>
    private async Task OnOptionsChangedAsync(PluginOptions newOptions)
    {
        try
        {
            foreach (var (pluginId, info) in _plugins)
            {
                var isSystemPlugin = newOptions.SystemPlugins.Contains(pluginId);
                var shouldBeEnabled = GetPluginEnabledState(pluginId, isSystemPlugin, info.Plugin.Metadata.EnabledByDefault)
                    && HasRequiredPermissions(info.Plugin.Metadata, isSystemPlugin, newOptions);

                if (shouldBeEnabled && !info.IsEnabled)
                {
                    _logger.Info("配置变更：启用插件 {Id}", pluginId);
                    try { await EnablePluginAsync(pluginId); }
                    catch (Exception ex) { _logger.Error(ex, "配置变更启用插件 {Id} 失败", pluginId); }
                }
                else if (!shouldBeEnabled && info.IsEnabled)
                {
                    _logger.Info("配置变更：禁用插件 {Id}", pluginId);
                    try { await DisablePluginAsync(pluginId); }
                    catch (Exception ex) { _logger.Error(ex, "配置变更禁用插件 {Id} 失败", pluginId); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理插件配置变更失败");
        }
    }

    /// <summary>
    /// 发现并加载所有插件
    /// </summary>
    public void DiscoverPlugins()
    {
        if (!_options.Enabled)
        {
            _logger.Info("插件系统已禁用");
            return;
        }

        // 1. 加载内置插件（直接引用的插件）
        LoadBuiltInPlugins();

        // 2. 加载外部插件（从 plugins 目录）
        LoadExternalPlugins();

        _logger.Info("共发现 {Count} 个插件", _plugins.Count);
    }

    /// <summary>
    /// 加载内置插件
    /// </summary>
    private void LoadBuiltInPlugins()
    {
        // 扫描当前程序集及其依赖中实现 ILyWafPlugin 的类型
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !a.FullName!.StartsWith("System") && !a.FullName.StartsWith("Microsoft"));

        foreach (var assembly in assemblies)
        {
            try
            {
                LoadPluginsFromAssembly(assembly, null, null);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "扫描程序集 {Assembly} 失败", assembly.FullName);
            }
        }
    }

    /// <summary>
    /// 加载外部插件
    /// </summary>
    private void LoadExternalPlugins()
    {
        var pluginDir = Path.GetFullPath(_options.PluginDirectory);
        if (!Directory.Exists(pluginDir))
        {
            _logger.Debug("插件目录不存在: {Directory}", pluginDir);
            return;
        }

        // 兼容旧布局时仅加载根目录 DLL；包管理布局只加载 current.json 指向的版本，
        // 避免历史版本和依赖程序集被当作独立插件重复扫描。
        var pluginFiles = Directory.GetFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly).ToList();
        foreach (var pointerFile in Directory.GetFiles(pluginDir, "current.json", SearchOption.AllDirectories))
        {
            var pluginRoot = Path.GetDirectoryName(pointerFile)!;
            var pointer = PluginPackageManager.ReadPointer(pluginRoot);
            if (pointer == null) continue;
            var entry = Path.GetFullPath(Path.Combine(pluginRoot, "versions", pointer.Version, pointer.EntryAssembly));
            var versionRoot = Path.GetFullPath(Path.Combine(pluginRoot, "versions", pointer.Version)) + Path.DirectorySeparatorChar;
            if (!entry.StartsWith(versionRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            {
                _logger.Warn("插件 {Id} 当前版本入口无效: {Entry}", pointer.Id, entry);
                continue;
            }
            pluginFiles.Add(entry);
        }

        foreach (var pluginFile in pluginFiles)
        {
            try
            {
                // 使用独立的 AssemblyLoadContext 加载（支持卸载）
                var loadContext = new PluginLoadContext(pluginFile);
                var assembly = loadContext.LoadFromAssemblyPath(pluginFile);

                LoadPluginsFromAssembly(assembly, loadContext, pluginFile);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "加载插件 {File} 失败", pluginFile);
            }
        }
    }

    /// <summary>
    /// 从程序集加载插件
    /// </summary>
    /// <param name="assembly">程序集</param>
    /// <param name="loadContext">加载上下文（外部插件用于卸载）</param>
    /// <param name="assemblyPath">程序集路径</param>
    private void LoadPluginsFromAssembly(Assembly assembly, AssemblyLoadContext? loadContext, string? assemblyPath)
    {
        var pluginTypes = assembly.GetTypes()
            .Where(t => typeof(ILyWafPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

        foreach (var pluginType in pluginTypes)
        {
            try
            {
                if (Activator.CreateInstance(pluginType) is not ILyWafPlugin plugin)
                {
                    continue;
                }

                var metadata = plugin.Metadata;
                if (assemblyPath != null) PluginPackageManager.ValidateLoadedMetadata(assemblyPath, metadata);

                // 检查是否是系统插件（仅由 SystemPlugins 配置决定）
                var isSystemPlugin = _options.SystemPlugins.Contains(metadata.Id);

                var grantedPermissions = isSystemPlugin
                    ? PluginPermission.All
                    : _options.Permissions.GetValueOrDefault(metadata.Id, PluginPermission.None);
                var missingPermissions = metadata.RequestedPermissions & ~grantedPermissions;

                // 检查是否已加载
                if (_plugins.TryGetValue(metadata.Id, out _))
                {
                    // 如果是系统插件，不允许被覆盖
                    if (isSystemPlugin)
                    {
                        _logger.Warn("插件 {Id} 是系统插件，不允许被外部插件覆盖", metadata.Id);
                        continue;
                    }
                    _logger.Warn("插件 {Id} 已存在，跳过加载", metadata.Id);
                    continue;
                }

                // 检查是否被禁用
                // 优先级：PluginConfigs 中的 enable/enabled/Enabled > DisabledPlugins > EnabledByDefault
                bool isEnabled = GetPluginEnabledState(metadata.Id, isSystemPlugin, metadata.EnabledByDefault);
                if (missingPermissions != PluginPermission.None)
                {
                    isEnabled = false;
                    _logger.Warn("插件 {Id} 缺少权限 {Permissions}，已保持禁用", metadata.Id, missingPermissions);
                }

                var info = new PluginInfo
                {
                    Plugin = plugin,
                    Context = null!, // 稍后初始化
                    LoadContext = loadContext,
                    AssemblyPath = assemblyPath,
                    IsEnabled = isEnabled
                };

                _plugins[metadata.Id] = info;

                var pluginTypeLabel = isSystemPlugin ? "系统插件" : "插件";
                _logger.Info("已加载{Type}: {Name} v{Version} ({Id})", pluginTypeLabel, metadata.Name, metadata.Version, metadata.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "实例化插件类型 {Type} 失败", pluginType.FullName);
            }
        }
    }

    /// <summary>
    /// 配置所有插件的服务
    /// </summary>
    public void ConfigureServices(IServiceCollection services)
    {
        // 所有插件都注册服务，确保运行时启用插件时依赖已经存在。
        var enabledPlugins = _plugins.Values
            .Where(plugin => plugin.IsEnabled)
            .OrderBy(p => (int)p.Plugin.Metadata.Priority)
            .ThenBy(p => p.Plugin.Metadata.Id)
            .ToList();

        foreach (var info in enabledPlugins)
        {
            try
            {
                _logger.Debug("配置插件服务: {Id}", info.Plugin.Metadata.Id);
                info.Plugin.ConfigureServices(services, _configuration);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "配置插件 {Id} 服务失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 初始化所有插件
    /// </summary>
    public async Task InitializePluginsAsync(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        var enabledPlugins = GetEnabledPluginsSorted();

        foreach (var info in enabledPlugins)
        {
            try
            {
                // 创建插件上下文
                var context = new PluginContext(
                    info.Plugin.Metadata.Id,
                    serviceProvider,
                    _configuration,
                    this,
                    _eventBus,
                    Path.GetFullPath(_options.DataDirectory)
                );

                // 更新插件信息中的上下文
                _plugins[info.Plugin.Metadata.Id] = info with { Context = context };

                _logger.Debug("初始化插件: {Id}", info.Plugin.Metadata.Id);
                using var activity = StartPluginActivity(info.Plugin.Metadata.Id, "initialize");
                await ApplyDataMigrationsAsync(info.Plugin, context, CancellationToken.None);
                await RunLifecycleAsync(info.Plugin.InitializeAsync(context), CancellationToken.None);
                await PublishOperationAsync(info.Plugin.Metadata.Id, "initialize", true);
            }
            catch (Exception ex)
            {
                await PublishOperationAsync(info.Plugin.Metadata.Id, "initialize", false);
                _logger.Error(ex, "初始化插件 {Id} 失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 配置中间件
    /// </summary>
    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        var enabledPlugins = GetEnabledPluginsSorted();

        foreach (var info in enabledPlugins)
        {
            try
            {
                _logger.Debug("配置插件中间件: {Id}", info.Plugin.Metadata.Id);
                info.Plugin.ConfigureMiddleware(app);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "配置插件 {Id} 中间件失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 配置代理管道（所有优先级）
    /// </summary>
    public void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        var enabledPlugins = GetEnabledPluginsSorted();

        foreach (var info in enabledPlugins)
        {
            try
            {
                _logger.Debug("配置插件代理管道: {Id}", info.Plugin.Metadata.Id);
                info.Plugin.ConfigureProxyPipeline(proxyApp);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "配置插件 {Id} 代理管道失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 配置代理管道 - 高优先级（Highest, High）
    /// </summary>
    public void ConfigureProxyPipelineHigh(IApplicationBuilder proxyApp)
    {
        var highPriorityPlugins = GetEnabledPluginsSorted()
            .Where(p => p.Plugin.Metadata.Priority <= PluginPriority.High);

        foreach (var info in highPriorityPlugins)
        {
            try
            {
                _logger.Debug("配置高优先级插件代理管道: {Id} (Priority={Priority})",
                    info.Plugin.Metadata.Id, info.Plugin.Metadata.Priority);
                info.Plugin.ConfigureProxyPipeline(proxyApp);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "配置插件 {Id} 代理管道失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 配置代理管道 - 普通及低优先级（Normal, Low, Lowest）
    /// </summary>
    public void ConfigureProxyPipelineNormal(IApplicationBuilder proxyApp)
    {
        var normalPriorityPlugins = GetEnabledPluginsSorted()
            .Where(p => p.Plugin.Metadata.Priority > PluginPriority.High);

        foreach (var info in normalPriorityPlugins)
        {
            try
            {
                _logger.Debug("配置普通优先级插件代理管道: {Id} (Priority={Priority})",
                    info.Plugin.Metadata.Id, info.Plugin.Metadata.Priority);
                info.Plugin.ConfigureProxyPipeline(proxyApp);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "配置插件 {Id} 代理管道失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 启动所有插件
    /// </summary>
    public async Task StartPluginsAsync(CancellationToken cancellationToken)
    {
        var enabledPlugins = GetEnabledPluginsSorted();

        foreach (var info in enabledPlugins)
        {
            try
            {
                _logger.Debug("启动插件: {Id}", info.Plugin.Metadata.Id);
                using var activity = StartPluginActivity(info.Plugin.Metadata.Id, "start");
                await RunLifecycleAsync(info.Plugin.StartAsync(cancellationToken), cancellationToken);
                await PublishOperationAsync(info.Plugin.Metadata.Id, "start", true);

                await _eventBus.PublishAsync(new PluginStateChangedEvent
                {
                    PluginId = info.Plugin.Metadata.Id,
                    OldState = PluginState.Initialized,
                    NewState = PluginState.Running
                });
            }
            catch (Exception ex)
            {
                await PublishOperationAsync(info.Plugin.Metadata.Id, "start", false);
                _logger.Error(ex, "启动插件 {Id} 失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 停止所有插件
    /// </summary>
    public async Task StopPluginsAsync(CancellationToken cancellationToken)
    {
        // 按相反顺序停止
        var enabledPlugins = GetEnabledPluginsSorted().AsEnumerable().Reverse();

        foreach (var info in enabledPlugins)
        {
            try
            {
                _logger.Debug("停止插件: {Id}", info.Plugin.Metadata.Id);
                using var activity = StartPluginActivity(info.Plugin.Metadata.Id, "stop");
                await RunLifecycleAsync(info.Plugin.StopAsync(cancellationToken), cancellationToken);
                await PublishOperationAsync(info.Plugin.Metadata.Id, "stop", true);

                await _eventBus.PublishAsync(new PluginStateChangedEvent
                {
                    PluginId = info.Plugin.Metadata.Id,
                    OldState = PluginState.Running,
                    NewState = PluginState.Stopped
                });
            }
            catch (Exception ex)
            {
                await PublishOperationAsync(info.Plugin.Metadata.Id, "stop", false);
                _logger.Error(ex, "停止插件 {Id} 失败", info.Plugin.Metadata.Id);
            }
        }
    }

    /// <summary>
    /// 获取插件的启用状态
    /// 优先级：PluginConfigs 中的 enable/enabled/Enabled > DisabledPlugins > EnabledByDefault
    /// </summary>
    /// <param name="pluginId">插件 ID</param>
    /// <param name="isSystemPlugin">是否是系统插件</param>
    /// <param name="enabledByDefault">插件默认启用状态</param>
    /// <returns>插件是否启用</returns>
    private bool GetPluginEnabledState(string pluginId, bool isSystemPlugin, bool enabledByDefault)
    {
        // 1. 首先检查 PluginConfigs 中是否有该插件的配置
        if (_options.PluginConfigs.TryGetValue(pluginId, out var pluginConfig))
        {
            // 检查 enable/enabled/Enabled 值（不区分大小写）
            var enableValue = GetConfigValue(pluginConfig, "enable", "enabled", "Enabled", "Enable");
            if (enableValue != null)
            {
                return ParseBoolValue(enableValue);
            }
        }

        // 2. 检查是否在 DisabledPlugins 列表中
        if (_options.DisabledPlugins.Contains(pluginId))
        {
            return false;
        }

        // 3. 系统插件默认启用，非系统插件使用 EnabledByDefault
        return isSystemPlugin || enabledByDefault;
    }

    /// <summary>
    /// 从配置字典中获取值（支持多个键名）
    /// </summary>
    private static object? GetConfigValue(Dictionary<string, object> config, params string[] keys)
    {
        foreach (var key in keys)
        {
            // 尝试精确匹配
            if (config.TryGetValue(key, out var value))
            {
                return value;
            }

            // 尝试不区分大小写匹配
            var matchedKey = config.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (matchedKey != null)
            {
                return config[matchedKey];
            }
        }
        return null;
    }

    /// <summary>
    /// 解析布尔值
    /// </summary>
    private static bool ParseBoolValue(object value)
    {
        if (value is bool b)
            return b;

        var strValue = value.ToString()?.ToLower();
        return strValue switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => true // 默认为 true（存在配置但无法解析时）
        };
    }

    /// <summary>
    /// 获取按优先级排序的启用插件列表
    /// </summary>
    private List<PluginInfo> GetEnabledPluginsSorted()
    {
        return [.. _plugins.Values
            .Where(p => p.IsEnabled)
            .OrderBy(p => (int)p.Plugin.Metadata.Priority)
            .ThenBy(p => p.Plugin.Metadata.Id)];
    }

    #region IPluginManager 实现

    public ILyWafPlugin? GetPlugin(string pluginId)
    {
        return _plugins.TryGetValue(pluginId, out var info) ? info.Plugin : null;
    }

    public IReadOnlyList<ILyWafPlugin> GetAllPlugins()
    {
        return _plugins.Values.Select(p => p.Plugin).ToList();
    }

    /// <summary>
    /// 获取所有系统插件
    /// </summary>
    public IReadOnlyList<ILyWafPlugin> GetSystemPlugins()
    {
        return _plugins.Values
            .Where(p => _options.SystemPlugins.Contains(p.Plugin.Metadata.Id))
            .Select(p => p.Plugin)
            .ToList();
    }

    /// <summary>
    /// 检查插件是否是系统插件
    /// 系统插件仅由 PluginOptions.SystemPlugins 配置决定
    /// </summary>
    public bool IsSystemPlugin(string pluginId)
    {
        return _options.SystemPlugins.Contains(pluginId);
    }

    public async Task EnablePluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var info))
        {
            throw new ArgumentException($"插件不存在: {pluginId}");
        }

        if (info.IsEnabled)
        {
            return;
        }

        if (!HasRequiredPermissions(info.Plugin.Metadata, IsSystemPlugin(pluginId), _options))
            throw new UnauthorizedAccessException($"插件 {pluginId} 缺少其声明的宿主权限。");

        info.IsEnabled = true;
        _options.DisabledPlugins.Remove(pluginId);

        // 如果服务已初始化，尝试启动插件
        if (_serviceProvider != null)
        {
            var context = new PluginContext(
                pluginId,
                _serviceProvider,
                _configuration,
                this,
                _eventBus,
                Path.GetFullPath(_options.DataDirectory)
            );

            await ApplyDataMigrationsAsync(info.Plugin, context, CancellationToken.None);
            await RunLifecycleAsync(info.Plugin.InitializeAsync(context), CancellationToken.None);
            await RunLifecycleAsync(info.Plugin.StartAsync(CancellationToken.None), CancellationToken.None);
        }

        _logger.Info("已启用插件: {Id}", pluginId);
    }

    public async Task DisablePluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var info))
        {
            throw new ArgumentException($"插件不存在: {pluginId}");
        }

        if (!info.IsEnabled)
        {
            return;
        }

        await RunLifecycleAsync(info.Plugin.StopAsync(CancellationToken.None), CancellationToken.None);
        info.IsEnabled = false;
        _options.DisabledPlugins.Add(pluginId);

        _logger.Info("已禁用插件: {Id}", pluginId);
    }

    public async Task ReloadPluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var info))
        {
            throw new ArgumentException($"插件不存在: {pluginId}");
        }

        // 只有外部插件支持重载
        if (info.LoadContext == null || info.AssemblyPath == null)
        {
            throw new InvalidOperationException($"内置插件不支持重载: {pluginId}");
        }

        // 停止并卸载
        await RunLifecycleAsync(info.Plugin.StopAsync(CancellationToken.None), CancellationToken.None);
        info.LoadContext.Unload();
        _plugins.TryRemove(pluginId, out _);

        // 重新加载
        var loadContext = new PluginLoadContext(info.AssemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(info.AssemblyPath);
        LoadPluginsFromAssembly(assembly, loadContext, info.AssemblyPath);

        // 如果服务已初始化，启动新加载的插件
        if (_serviceProvider != null && _plugins.TryGetValue(pluginId, out var newInfo))
        {
            var context = new PluginContext(
                pluginId,
                _serviceProvider,
                _configuration,
                this,
                _eventBus,
                Path.GetFullPath(_options.DataDirectory)
            );

            await ApplyDataMigrationsAsync(newInfo.Plugin, context, CancellationToken.None);
            await RunLifecycleAsync(newInfo.Plugin.InitializeAsync(context), CancellationToken.None);
            await RunLifecycleAsync(newInfo.Plugin.StartAsync(CancellationToken.None), CancellationToken.None);
        }

        _logger.Info("已重载插件: {Id}", pluginId);
    }

    public PluginState GetPluginState(string pluginId)
    {
        return _plugins.TryGetValue(pluginId, out var info) ? info.Plugin.State : PluginState.Unloaded;
    }

    /// <inheritdoc />
    public bool IsPermissionGranted(string pluginId, PluginPermission permission)
    {
        if (_options.SystemPlugins.Contains(pluginId)) return true;
        return (_options.Permissions.GetValueOrDefault(pluginId, PluginPermission.None) & permission) == permission;
    }

    private static bool HasRequiredPermissions(PluginMetadata metadata, bool isSystemPlugin, PluginOptions options)
    {
        if (isSystemPlugin) return true;
        var granted = options.Permissions.GetValueOrDefault(metadata.Id, PluginPermission.None);
        return (granted & metadata.RequestedPermissions) == metadata.RequestedPermissions;
    }

    private async Task RunLifecycleAsync(Task operation, CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(_options.LifecycleTimeoutSeconds, 1, 300);
        await operation.WaitAsync(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    internal async Task ApplyDataMigrationsAsync(ILyWafPlugin plugin, IPluginContext context, CancellationToken cancellationToken)
    {
        var target = plugin.Metadata.DataVersion;
        if (target == 0) return;
        if (!IsPermissionGranted(plugin.Metadata.Id, PluginPermission.DataDirectory))
            throw new UnauthorizedAccessException($"插件 {plugin.Metadata.Id} 声明了数据版本但未获数据目录权限。");

        const string versionFile = ".lywaf-data-version";
        var current = 0;
        if (context.Storage.Exists(versionFile))
        {
            using var stream = context.Storage.OpenRead(versionFile);
            Span<byte> buffer = stackalloc byte[4];
            if (stream.Read(buffer) != buffer.Length) throw new InvalidDataException("插件数据版本文件损坏。");
            current = BitConverter.ToInt32(buffer);
        }
        if (current == target) return;
        if (plugin is not IPluginDataMigrationProvider provider)
            throw new InvalidOperationException($"插件 {plugin.Metadata.Id} 需要从数据版本 {current} 迁移到 {target}，但未提供迁移器。");

        var migrations = provider.GetMigrations();
        var visited = new HashSet<int>();
        while (current != target)
        {
            if (!visited.Add(current)) throw new InvalidOperationException("插件数据迁移链存在循环。");
            var migration = migrations.SingleOrDefault(item => item.FromVersion == current
                && (target > current ? item.ToVersion > current && item.ToVersion <= target : item.ToVersion < current && item.ToVersion >= target));
            if (migration == null) throw new InvalidOperationException($"插件数据迁移链在版本 {current} 处中断。");
            await RunLifecycleAsync(migration.MigrateAsync(context.Storage, cancellationToken), cancellationToken);
            current = migration.ToVersion;
        }

        using var output = context.Storage.OpenWrite(versionFile);
        output.Write(BitConverter.GetBytes(target));
        output.Flush();
    }

    private Task PublishOperationAsync(string pluginId, string operation, bool success)
        => _eventBus.PublishAsync(new PluginOperationEvent
        {
            PluginId = pluginId,
            Operation = operation,
            Success = success
        });

    private static System.Diagnostics.Activity? StartPluginActivity(string pluginId, string operation)
    {
        var activity = LyWafTelemetry.ActivitySource.StartActivity($"plugin.{operation}");
        activity?.SetTag("lywaf.plugin.id", pluginId);
        activity?.SetTag("lywaf.plugin.operation", operation);
        return activity;
    }

    #endregion
}

/// <summary>
/// 插件程序集加载上下文（支持卸载）
/// </summary>
public class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new AssemblyDependencyResolver(pluginPath);


    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 插件必须复用默认上下文中的契约程序集，否则 ILyWafPlugin 类型身份不相等。
        if (assemblyName.Name == typeof(ILyWafPlugin).Assembly.GetName().Name)
            return typeof(ILyWafPlugin).Assembly;
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }
        return IntPtr.Zero;
    }
}
