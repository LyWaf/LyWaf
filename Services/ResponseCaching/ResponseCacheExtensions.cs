using Microsoft.AspNetCore.Mvc;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Services.ResponseCaching;

/// <summary>注册响应缓存及受控制台认证保护的查询、清理端点。</summary>
public static class ResponseCacheExtensions
{
    public static IServiceCollection AddLyWafResponseCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResponseCacheOptions>(configuration.GetSection("ResponseCache"));
        services.AddHttpClient("response-cache-refresh", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<ResponseCacheStore>();
        services.AddSingleton<ResponseCacheRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<ResponseCacheRefreshService>());
        return services;
    }

    public static WebApplication MapLyWafResponseCacheApi(this WebApplication app, int controlPort)
    {
        app.MapGet("/api/response-cache", (HttpContext context, ResponseCacheStore store) =>
            Results.Ok(new { success = true, entries = store.GetSnapshots() })).RequireHost($"*:{controlPort}");
        app.MapPost("/api/response-cache/purge", ([FromBody] PurgeResponseCacheRequest request,
            HttpContext context, ResponseCacheStore store) =>
        {
            try { return Results.Ok(new { success = true, removed = store.Purge(request) }); }
            catch (ArgumentException ex)
            {
                return ManagementApiContract.Error(context, StatusCodes.Status400BadRequest,
                    "invalid_purge_filter", ex.Message);
            }
        }).RequireHost($"*:{controlPort}");
        return app;
    }
}
