using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Configuration;

namespace LyWaf.Services.Discovery;

/// <summary>服务发现依赖注入与 IConfiguration 触发源注册。</summary>
public static class DiscoveryExtensions
{
    public static IServiceCollection AddLyWafServiceDiscovery(
        this IServiceCollection services,
        IConfigurationManager configuration)
    {
        var signal = new DiscoveryReloadSignal();
        configuration.Add(signal);
        services.Configure<ServiceDiscoveryOptions>(configuration.GetSection("ServiceDiscovery"));
        services.AddSingleton(signal);
        services.AddSingleton<DiscoveryRegistry>();
        services.AddSingleton<IServiceDiscoveryProvider, FileDiscoveryProvider>();
        services.AddSingleton<IServiceDiscoveryProvider, DnsDiscoveryProvider>();
        services.AddSingleton<IServiceDiscoveryProvider, DockerDiscoveryProvider>();
        services.AddSingleton<IServiceDiscoveryProvider, KubernetesDiscoveryProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProxyConfigFilter, DiscoveryProxyConfigFilter>());
        services.AddHostedService<DiscoveryCoordinator>();
        return services;
    }
}
