namespace LyWaf.Services.Metrics;

/// <summary>Registers metric collectors while leaving conventional middleware construction to the request pipeline.</summary>
public static class MetricsExtensions
{
    /// <summary>Registers the shared metrics registry without registering <see cref="MetricsMiddleware"/> as a DI service.</summary>
    public static IServiceCollection AddLyWafMetrics(this IServiceCollection services)
    {
        services.AddSingleton<PrometheusMetrics>();
        return services;
    }
}
