using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Sys.SiteAuth;

/// <summary>为代理站点提供与控制台身份体系完全隔离的访问认证。</summary>
public sealed class SiteAuthPlugin : LyWafPluginBase, IPluginApiHandler
{
    public override PluginMetadata Metadata => new()
    {
        Id = "site-auth", Name = "受保护站点认证", Version = "1.0.0",
        Description = "按域名和路径提供 Form、Basic 与 OAuth2 认证",
        Author = "LyWaf Team", Priority = PluginPriority.Highest, EnabledByDefault = false,
        RequestedPermissions = PluginPermission.Network | PluginPermission.SensitiveConfiguration | PluginPermission.ConsoleApi,
        DefaultOptions = new SiteAuthOptions(),
    };

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        base.ConfigureServices(services, configuration);
        services.AddSingleton<SiteAuthService>();
        services.AddSingleton<SiteAuthAdminService>();
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp) =>
        proxyApp.UseMiddleware<SiteAuthMiddleware>();

    public Task<IResult> HandleApiAsync(string path, HttpContext context) =>
        context.RequestServices.GetRequiredService<SiteAuthAdminService>()
            .HandleAsync(path.Trim('/'), context);
}
