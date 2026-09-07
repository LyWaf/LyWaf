using LyWaf.Middleware;
using LyWaf.Plugins.Core;
using LyWaf.Services.ExternalAuthorization;
using LyWaf.Services.Metrics;
using LyWaf.Services.RequestMirroring;
using LyWaf.Services.ResponseCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class MiddlewareRegistrationTests
{
    [Fact]
    public void ConventionalMiddlewareIsCreatedByPipelineNotDependencyInjection()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLyWafMetrics();
        services.AddLyWafRequestMirroring(configuration);
        services.AddLyWafExternalAuthorization(configuration);
        services.AddLyWafResponseCache(configuration);

        var forbidden = new[]
        {
            typeof(MetricsMiddleware), typeof(RequestMirrorMiddleware),
            typeof(ExternalAuthorizationMiddleware), typeof(ResponseCacheMiddleware)
        };
        Assert.DoesNotContain(services, descriptor => forbidden.Contains(descriptor.ServiceType));
    }

    [Fact]
    public void FactoryMiddlewareRemainsRegisteredForPerRequestActivation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ResponseCompressMiddleware>();
        services.AddSingleton<BasePluginMiddleware>();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ResponseCompressMiddleware));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(BasePluginMiddleware));
    }
}
