using Microsoft.AspNetCore.Mvc;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Auth;

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
            TypedResults.Ok(ManagementLists.From(store.GetSnapshots()).Sort("key", item => item.Key)
                .Sort("host", item => item.Host).Sort("path", item => item.Path).Sort("length", item => item.Length)
                .Sort("storedAt", item => item.StoredAt).Sort("lastAccessedAt", item => item.LastAccessedAt)
                .Search(item => $"{item.Host} {item.Path} {item.Key}").Timestamp(item => item.StoredAt)
                .Read(context.Request.Query, "storedAt", true))).RequireHost($"*:{controlPort}").RequireManagementPermission();
        app.MapPost("/api/response-cache/purge", ([FromBody] PurgeResponseCacheRequest request,
            HttpContext context, ResponseCacheStore store) =>
        {
            return TypedResults.Ok(new { success = true, removed = store.Purge(request) });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();
        return app;
    }
}
