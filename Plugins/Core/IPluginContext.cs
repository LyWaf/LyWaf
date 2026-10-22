using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Core;

/// <summary>
/// 插件上下文实现
/// </summary>
public class PluginContext : IPluginContext
{
    private readonly IPluginManager _pluginManager;
    private readonly string _pluginId;
    private readonly IPluginEventBus _eventBus;
    
    public IServiceProvider Services { get; }
    public IConfiguration Configuration { get; }
    public NLog.Logger Logger { get; }
    public string DataDirectory { get; }
    public IPluginTaskCoordinator Tasks { get; }
    public IPluginStorage Storage { get; }
    public IPluginNetworkClientFactory Network { get; }
    public IPluginNodeMessageClient NodeMessages { get; }
    public IPluginNodeRequestClient NodeRequests { get; }
    
    public PluginContext(
        string pluginId,
        IServiceProvider services,
        IConfiguration configuration,
        IPluginManager pluginManager,
        IPluginEventBus eventBus,
        string baseDataDirectory)
    {
        _pluginId = pluginId;
        _pluginManager = pluginManager;
        _eventBus = eventBus;
        Configuration = CreatePluginConfiguration(configuration, pluginId,
            pluginManager.IsPermissionGranted(pluginId, PluginPermission.SensitiveConfiguration));
        Logger = NLog.LogManager.GetLogger($"Plugin.{pluginId}");
        DataDirectory = Path.Combine(baseDataDirectory, pluginId);
        Tasks = services.GetRequiredService<IPluginTaskCoordinator>();
        
        // 确保数据目录存在
        if (!Directory.Exists(DataDirectory))
        {
            Directory.CreateDirectory(DataDirectory);
        }
        Storage = new PluginStorage(DataDirectory,
            pluginManager.IsPermissionGranted(pluginId, PluginPermission.DataDirectory));
        Network = new PluginNetworkClientFactory(services.GetRequiredService<IHttpClientFactory>(),
            pluginManager.IsPermissionGranted(pluginId, PluginPermission.Network));
        NodeMessages = services.GetService<LyWaf.Services.NodeCommunication.PluginNodeMessageService>()?.Bind(pluginId)
            ?? new UnavailableNodeMessages();
        NodeRequests = services.GetService<LyWaf.Services.NodeCommunication.PluginNodeRequestClient>()?.Bind(pluginId)
            ?? new UnavailableNodeRequests();
        Services = new ContextServices(this);
    }

    private static IConfiguration CreatePluginConfiguration(IConfiguration source, string pluginId, bool includeSensitive)
    {
        var values = source.GetSection($"Plugins:{pluginId}").AsEnumerable()
            .Where(pair => pair.Value != null)
            .Where(pair => includeSensitive || !pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Contains("key", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(values!).Build();
    }
    
    public T GetPluginConfig<T>(string? sectionName = null) where T : class, new()
    {
        var section = sectionName ?? $"Plugins:{_pluginId}";
        var config = new T();
        Configuration.GetSection(section).Bind(config);
        return config;
    }
    
    public ILyWafPlugin? GetPlugin(string pluginId)
    {
        return _pluginManager.GetPlugin(pluginId);
    }
    
    public IReadOnlyList<ILyWafPlugin> GetAllPlugins()
    {
        return _pluginManager.GetAllPlugins();
    }
    
    public Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : class
    {
        return _eventBus.PublishAsync(@event);
    }
    
    public IDisposable SubscribeEvent<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        return _eventBus.Subscribe(handler);
    }

    private sealed class UnavailableNodeMessages : IPluginNodeMessageClient
    {
        public Task<IReadOnlyList<PluginNodeDelivery>> SendAsync(string messageType, int schemaVersion, string targetGroup,
            System.Text.Json.JsonElement payload, string messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Host node messaging is unavailable");
    }

    private sealed class UnavailableNodeRequests : IPluginNodeRequestClient
    {
        public Task<PluginNodeResponse> RequestAsync(string targetNodeId, string messageType, int schemaVersion,
            System.Text.Json.JsonElement payload, string messageId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Host node requests are unavailable");
    }

    // Deliberately has no fallback to the host provider, including scopes and IEnumerable<T>.
    private sealed class ContextServices(PluginContext context) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceProvider)) return this;
            if (serviceType == typeof(IConfiguration)) return context.Configuration;
            if (serviceType == typeof(IPluginContext)) return context;
            if (serviceType == typeof(IPluginTaskCoordinator)) return context.Tasks;
            if (serviceType == typeof(IPluginStorage)) return context.Storage;
            if (serviceType == typeof(IPluginNetworkClientFactory)) return context.Network;
            if (serviceType == typeof(IPluginNodeMessageClient)) return context.NodeMessages;
            if (serviceType == typeof(IPluginNodeRequestClient)) return context.NodeRequests;
            if (serviceType == typeof(NLog.Logger)) return context.Logger;
            return null;
        }
    }
}

/// <summary>Implements plugin storage with canonical path containment and streaming handles.</summary>
public sealed class PluginStorage(string rootDirectory, bool granted) : IPluginStorage
{
    private readonly string _root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;

    /// <inheritdoc />
    public Stream OpenRead(string relativePath) => new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <inheritdoc />
    public Stream OpenWrite(string relativePath, bool overwrite = true)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous);
    }

    /// <inheritdoc />
    public void Delete(string relativePath) => File.Delete(Resolve(relativePath));
    /// <inheritdoc />
    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    private string Resolve(string relativePath)
    {
        if (!granted) throw new UnauthorizedAccessException("插件未获专属数据目录权限。");
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("只允许相对路径。", nameof(relativePath));
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("路径超出插件专属数据目录。");
        return path;
    }
}

/// <summary>Guards host-managed outbound HTTP client creation with the declared network permission.</summary>
public sealed class PluginNetworkClientFactory(IHttpClientFactory factory, bool granted) : IPluginNetworkClientFactory
{
    /// <inheritdoc />
    public HttpClient CreateClient() => granted
        ? factory.CreateClient("LyWaf.Plugin")
        : throw new UnauthorizedAccessException("插件未获网络访问权限。");
}
