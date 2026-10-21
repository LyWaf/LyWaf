using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace LyWaf.Services.Configuration;

public interface IRestorableConfigurationProvider
{
    void RestoreValues(IReadOnlyDictionary<string, string?> values);
}

/// <summary>Republishes restored configuration after all instantiated option caches are cleared.</summary>
public sealed class ConfigurationReloadRecovery
{
    private readonly ConcurrentDictionary<Type, Action> _clear = new();
    private ConfigurationReloadToken _token = new();

    internal void Register<T>(IOptionsMonitorCache<T> cache) where T : class => _clear[typeof(T)] = cache.Clear;
    internal IChangeToken Token => Volatile.Read(ref _token);

    internal void NotifyRestored()
    {
        // Dependent post-configurers may read another monitor before its own callback runs.
        foreach (var clear in _clear.Values) clear();
        Interlocked.Exchange(ref _token, new ConfigurationReloadToken()).OnReload();
    }
}

internal sealed class ConfigurationRecoveryChangeTokenSource<T> : IOptionsChangeTokenSource<T> where T : class
{
    private readonly ConfigurationReloadRecovery _recovery;
    public ConfigurationRecoveryChangeTokenSource(ConfigurationReloadRecovery recovery, IOptionsMonitorCache<T> cache)
    {
        _recovery = recovery;
        recovery.Register(cache);
    }
    public string Name => Options.DefaultName;
    public IChangeToken GetChangeToken() => _recovery.Token;
}

public static class ConfigurationReloadRecoveryExtensions
{
    public static IServiceCollection AddConfigurationReloadRecovery(this IServiceCollection services)
    {
        services.TryAddSingleton<ConfigurationReloadRecovery>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IOptionsChangeTokenSource<>),
            typeof(ConfigurationRecoveryChangeTokenSource<>)));
        return services;
    }
}
