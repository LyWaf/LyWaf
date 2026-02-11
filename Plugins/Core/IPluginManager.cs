using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLog;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件管理器接口
/// </summary>
public interface IPluginManager
{
    /// <summary>
    /// 获取指定插件
    /// </summary>
    ILyWafPlugin? GetPlugin(string pluginId);

    /// <summary>
    /// 获取所有已加载的插件
    /// </summary>
    IReadOnlyList<ILyWafPlugin> GetAllPlugins();

    /// <summary>
    /// 获取所有系统插件
    /// </summary>
    IReadOnlyList<ILyWafPlugin> GetSystemPlugins();

    /// <summary>
    /// 检查插件是否是系统插件
    /// </summary>
    bool IsSystemPlugin(string pluginId);

    /// <summary>
    /// 启用插件
    /// </summary>
    Task EnablePluginAsync(string pluginId);

    /// <summary>
    /// 禁用插件
    /// </summary>
    Task DisablePluginAsync(string pluginId);

    /// <summary>
    /// 重新加载插件
    /// </summary>
    Task ReloadPluginAsync(string pluginId);

    /// <summary>
    /// 获取插件状态
    /// </summary>
    PluginState GetPluginState(string pluginId);
}

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

    /// <summary>
    /// 各插件的配置
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> PluginConfigs { get; set; } = [];

    /// <summary>
    /// 系统插件 ID 列表
    /// 系统插件是 LyWaf 自带的插件，默认加载，不会被外部插件覆盖
    /// 除非在 DisabledPlugins 中明确禁用
    /// </summary>
    public HashSet<string> SystemPlugins { get; } = ["request-logger", "analysis"];
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
                var shouldBeEnabled = GetPluginEnabledState(pluginId, isSystemPlugin, info.Plugin.Metadata.EnabledByDefault);

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

        // 扫描插件目录下的所有 DLL
        var pluginFiles = Directory.GetFiles(pluginDir, "*.dll", SearchOption.AllDirectories);

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

                // 检查是否是系统插件（仅由 SystemPlugins 配置决定）
                var isSystemPlugin = _options.SystemPlugins.Contains(metadata.Id);

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
        var enabledPlugins = GetEnabledPluginsSorted();

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
                await info.Plugin.InitializeAsync(context);
            }
            catch (Exception ex)
            {
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
                await info.Plugin.StartAsync(cancellationToken);

                await _eventBus.PublishAsync(new PluginStateChangedEvent
                {
                    PluginId = info.Plugin.Metadata.Id,
                    OldState = PluginState.Initialized,
                    NewState = PluginState.Running
                });
            }
            catch (Exception ex)
            {
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
                await info.Plugin.StopAsync(cancellationToken);

                await _eventBus.PublishAsync(new PluginStateChangedEvent
                {
                    PluginId = info.Plugin.Metadata.Id,
                    OldState = PluginState.Running,
                    NewState = PluginState.Stopped
                });
            }
            catch (Exception ex)
            {
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

            await info.Plugin.InitializeAsync(context);
            await info.Plugin.StartAsync(CancellationToken.None);
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

        await info.Plugin.StopAsync(CancellationToken.None);
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
        await info.Plugin.StopAsync(CancellationToken.None);
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

            await newInfo.Plugin.InitializeAsync(context);
            await newInfo.Plugin.StartAsync(CancellationToken.None);
        }

        _logger.Info("已重载插件: {Id}", pluginId);
    }

    public PluginState GetPluginState(string pluginId)
    {
        return _plugins.TryGetValue(pluginId, out var info) ? info.Plugin.State : PluginState.Unloaded;
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
