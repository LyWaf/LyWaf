namespace LyWaf.Services.ExternalAuthorization;

/// <summary>注册外部授权调用点及其有界缓存。</summary>
public static class ExternalAuthorizationExtensions
{
    public static IServiceCollection AddLyWafExternalAuthorization(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ExternalAuthorizationOptions>()
            .Bind(configuration.GetSection("ExternalAuthorization"))
            .Validate(static value => value.TimeoutMilliseconds is >= 50 and <= 30000 &&
                                      value.MaxBodyBytes is >= 0 and <= 1048576 &&
                                      value.MaxResponseBytes is >= 1024 and <= 1048576 &&
                                      value.MaxHeaderBytes is >= 0 and <= 1048576 &&
                                      value.CacheSeconds is >= 0 and <= 3600 &&
                                      value.MaxCacheEntries is >= 1 and <= 100000 &&
                                      value.AllowedRequestHeaders != null && value.AllowedUpstreamHeaders != null &&
                                      value.CacheKeyHeaders != null,
                "ExternalAuthorization 资源限制配置无效。")
            .ValidateOnStart();
        services.AddHttpClient("external-authorization", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ExternalAuthorizationCache>();
        return services;
    }
}
