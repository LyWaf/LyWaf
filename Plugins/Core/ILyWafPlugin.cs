using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Core;

/// <summary>Declares configuration display and reload requirements for host management tools.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PluginConfigurationAttribute : Attribute
{
    public bool Sensitive { get; set; }
    public string? LabelKey { get; set; }
    public string? DescriptionKey { get; set; }
    public bool RequiresRestart { get; set; }
    public PluginConfigurationScope Scopes { get; set; } = PluginConfigurationScope.All;
}

/// <summary>Configuration levels accepted by an individual plugin field.</summary>
[Flags]
public enum PluginConfigurationScope { Global = 1, Site = 2, Route = 4, All = 7 }

/// <summary>Returns detached effective options for the current request.</summary>
public interface IPluginRequestConfiguration
{
    T Get<T>(Microsoft.AspNetCore.Http.HttpContext context, string pluginId) where T : class;
}

/// <summary>
/// 插件优先级
/// </summary>
public enum PluginPriority
{
    /// <summary>最高优先级（如安全相关）</summary>
    Highest = 0,
    /// <summary>高优先级</summary>
    High = 100,
    /// <summary>正常优先级</summary>
    Normal = 500,
    /// <summary>低优先级</summary>
    Low = 900,
    /// <summary>最低优先级</summary>
    Lowest = 1000
}

/// <summary>
/// 插件状态
/// </summary>
public enum PluginState
{
    /// <summary>未加载</summary>
    Unloaded,
    /// <summary>已加载</summary>
    Loaded,
    /// <summary>已初始化</summary>
    Initialized,
    /// <summary>运行中</summary>
    Running,
    /// <summary>已停止</summary>
    Stopped,
    /// <summary>错误</summary>
    Error
}

/// <summary>Declares privileged host capabilities a plugin intends to use.</summary>
[Flags]
public enum PluginPermission
{
    /// <summary>No privileged capability is requested.</summary>
    None = 0,
    /// <summary>Allows outbound network access through host-provided clients.</summary>
    Network = 1,
    /// <summary>Allows persistence inside the plugin's dedicated data directory.</summary>
    DataDirectory = 2,
    /// <summary>Allows handling requests through the unified console API proxy.</summary>
    ConsoleApi = 4,
    /// <summary>Allows reading configuration values classified as sensitive.</summary>
    SensitiveConfiguration = 8,
    /// <summary>Allows declared node messages through the host's group-scoped communication service.</summary>
    NodeMessaging = 16,
    /// <summary>Represents every currently defined permission.</summary>
    All = Network | DataDirectory | ConsoleApi | SensitiveConfiguration | NodeMessaging
}

/// <summary>
/// 插件元数据
/// </summary>
public class PluginMetadata
{
    /// <summary>Opt in only when request handlers consume IPluginRequestConfiguration.</summary>
    public bool SupportsScopedConfiguration { get; init; }
    /// <summary>插件唯一标识</summary>
    public required string Id { get; init; }
    
    /// <summary>插件名称</summary>
    public required string Name { get; init; }
    
    /// <summary>插件版本</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Gets the schema version of persistent plugin data.</summary>
    public int DataVersion { get; init; }

    /// <summary>Gets the frontend asset version that must match this backend plugin.</summary>
    public string? FrontendVersion { get; init; }
    
    /// <summary>插件描述</summary>
    public string Description { get; init; } = "";
    
    /// <summary>作者</summary>
    public string Author { get; init; } = "";
    
    /// <summary>插件优先级</summary>
    public PluginPriority Priority { get; init; } = PluginPriority.Normal;
    
    /// <summary>依赖的其他插件 ID 列表</summary>
    public string[] Dependencies { get; init; } = [];
    
    /// <summary>是否默认启用</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Gets the privileged capabilities required before the plugin can be enabled.</summary>
    public PluginPermission RequestedPermissions { get; init; } = PluginPermission.None;
    /// <summary>Exact message/schema/group capabilities; host policy must also grant them.</summary>
    public IReadOnlyList<PluginNodeMessageCapability> NodeMessages { get; init; } = [];
    public IReadOnlyList<PluginNodeRequestCapability> NodeRequests { get; init; } = [];

    /// <summary>
    /// 插件默认配置选项实例
    /// 设置后前端可读取默认属性并提供编辑界面
    /// 例如: DefaultOptions = new ImageProcessOptions()
    /// </summary>
    public object? DefaultOptions { get; init; }
    /// <summary>Embedded JSON resource prefix; defaults to the plugin namespace plus .Localization.</summary>
    public string? LocalizationResourcePrefix { get; init; }
}

/// <summary>
/// 插件操作定义
/// 用于声明插件支持的可操作功能（如查看日志、清空缓存等）
/// </summary>
public class PluginAction
{
    /// <summary>操作唯一标识（如 "view-logs"）</summary>
    public required string Id { get; init; }

    /// <summary>操作显示名称（如 "查看日志"）</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 操作类型，前端根据类型渲染不同 UI
    /// "log-viewer" = 日志查看器（分页、搜索、展开详情）
    /// "button" = 一次性执行按钮（如清空缓存）
    /// </summary>
    public string Type { get; init; } = "button";

    /// <summary>操作说明</summary>
    public string? Description { get; init; }

    /// <summary>page-link 类型操作对应的前端路由。</summary>
    public string? Path { get; init; }
}

/// <summary>Provides ordered migrations for a plugin's persistent data directory.</summary>
public interface IPluginDataMigrationProvider
{
    /// <summary>Gets migrations ordered or orderable by their source version.</summary>
    IReadOnlyList<IPluginDataMigration> GetMigrations();
}

/// <summary>Moves plugin-owned persistent data between two adjacent schema versions.</summary>
public interface IPluginDataMigration
{
    /// <summary>Gets the source data schema version.</summary>
    int FromVersion { get; }
    /// <summary>Gets the destination data schema version.</summary>
    int ToVersion { get; }
    /// <summary>Runs the migration using storage constrained to the plugin directory.</summary>
    Task MigrateAsync(IPluginStorage storage, CancellationToken cancellationToken);
}

/// <summary>
/// 插件操作执行结果
/// </summary>
public class PluginActionResult
{
    /// <summary>获取或设置操作是否成功。</summary>
    public bool Success { get; set; } = true;
    /// <summary>获取或设置供控制台展示的结果消息。</summary>
    public string? Message { get; set; }
    /// <summary>JSON 可序列化的数据（具体结构取决于操作类型）</summary>
    public object? Data { get; set; }
}

/// <summary>
/// LyWaf 插件基础接口
/// 所有插件必须实现此接口
/// </summary>
public interface ILyWafPlugin
{
    /// <summary>
    /// 获取插件元数据
    /// </summary>
    PluginMetadata Metadata { get; }

    /// <summary>
    /// 当前插件状态
    /// </summary>
    PluginState State { get; }

    /// <summary>
    /// 配置服务（在 DI 容器构建前调用）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// 初始化插件（在 DI 容器构建后调用）
    /// </summary>
    /// <param name="context">插件上下文</param>
    Task InitializeAsync(IPluginContext context);

    /// <summary>
    /// 配置中间件管道
    /// </summary>
    /// <param name="app">应用程序构建器</param>
    void ConfigureMiddleware(IApplicationBuilder app);

    /// <summary>
    /// 配置 YARP 反向代理管道（在 MapReverseProxy 内调用）
    /// </summary>
    /// <param name="proxyApp">代理应用程序构建器</param>
    void ConfigureProxyPipeline(IApplicationBuilder proxyApp);

    /// <summary>
    /// 启动插件
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 停止插件
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 获取插件支持的操作列表
    /// 返回空列表表示无自定义操作
    /// </summary>
    IReadOnlyList<PluginAction> GetActions() => [];

    /// <summary>
    /// 执行插件操作
    /// </summary>
    /// <param name="actionId">操作 ID</param>
    /// <param name="parameters">操作参数</param>
    Task<PluginActionResult> ExecuteActionAsync(string actionId, Dictionary<string, string>? parameters)
        => Task.FromResult(new PluginActionResult { Success = false, Message = $"操作 {actionId} 未实现" });
}

/// <summary>由插件实现，用于接收插件系统统一代理入口分派的控制台请求。</summary>
public interface IPluginApiHandler
{
    /// <summary>处理插件统一代理入口转发的相对路径请求。</summary>
    /// <param name="path">去除插件代理前缀后的相对路径。</param>
    /// <param name="context">当前 HTTP 请求上下文。</param>
    /// <returns>可由 ASP.NET Core 执行的响应结果。</returns>
    Task<IResult> HandleApiAsync(string path, HttpContext context);
}

/// <summary>
/// 插件基类，提供默认实现
/// </summary>
public abstract class LyWafPluginBase : ILyWafPlugin
{
    /// <inheritdoc />
    public abstract PluginMetadata Metadata { get; }

    /// <inheritdoc />
    public PluginState State { get; protected set; } = PluginState.Unloaded;

    /// <summary>获取插件初始化后可用的宿主上下文。</summary>
    protected IPluginContext? Context { get; private set; }

    /// <inheritdoc />
    public virtual void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // 如果设置了 DefaultOptions，自动注册 services.Configure<T>(section)
        if (Metadata.DefaultOptions is { } options)
        {
            var section = configuration.GetSection($"Plugins:{Metadata.Id}");
            // 反射调用 services.Configure<T>(IConfigurationSection)
            typeof(OptionsConfigurationServiceCollectionExtensions)
                .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                .First(m => m.Name == "Configure" && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(IConfiguration))
                .MakeGenericMethod(options.GetType())
                .Invoke(null, [services, section]);
        }
    }

    /// <inheritdoc />
    public virtual Task InitializeAsync(IPluginContext context)
    {
        Context = context;
        // 自动将配置绑定到 DefaultOptions 实例（清空 List/Dictionary 后再 Bind，避免重复）
        BindOptionsFromConfig(context.Configuration);
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 将配置绑定到 Metadata.DefaultOptions 实例上
    /// 自动清空 List/Dictionary 属性，避免 Bind() 追加导致重复
    /// </summary>
    private void BindOptionsFromConfig(IConfiguration configuration)
    {
        var options = Metadata.DefaultOptions;
        if (options == null) return;
        // Bind() 对 List/Dictionary 是追加操作，必须先清空
        foreach (var prop in options.GetType().GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            object? value;
            try { value = prop.GetValue(options); } catch { continue; }
            if (value is System.Collections.IList list) list.Clear();
            else if (value is System.Collections.IDictionary dict) dict.Clear();
        }
        configuration.GetSection($"Plugins:{Metadata.Id}").Bind(options);
    }
    
    /// <inheritdoc />
    public virtual void ConfigureMiddleware(IApplicationBuilder app)
    {
        // 默认不配置中间件
    }
    
    /// <inheritdoc />
    public virtual void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        // 默认不配置代理管道
    }
    
    /// <inheritdoc />
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        State = PluginState.Running;
        return Task.CompletedTask;
    }
    
    /// <inheritdoc />
    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        State = PluginState.Stopped;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取插件支持的操作列表（子类可重写）
    /// </summary>
    public virtual IReadOnlyList<PluginAction> GetActions() => [];

    /// <summary>
    /// 执行插件操作（子类可重写）
    /// </summary>
    public virtual Task<PluginActionResult> ExecuteActionAsync(string actionId, Dictionary<string, string>? parameters)
        => Task.FromResult(new PluginActionResult { Success = false, Message = $"操作 {actionId} 未实现" });
}
