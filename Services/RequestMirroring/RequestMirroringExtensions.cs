namespace LyWaf.Services.RequestMirroring;

/// <summary>注册请求镜像策略、HTTP 客户端和有界后台执行器。</summary>
public static class RequestMirroringExtensions
{
    public static IServiceCollection AddLyWafRequestMirroring(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RequestMirroringOptions>(configuration.GetSection("RequestMirroring"));
        services.AddHttpClient("request-mirror", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<RequestMirrorDispatcher>();
        services.AddHostedService(static provider => provider.GetRequiredService<RequestMirrorDispatcher>());
        return services;
    }
}
