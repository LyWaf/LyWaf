using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LyWaf.Services.NodeCommunication;

public static class PluginNodeMessageRegistration
{
    public static IServiceCollection AddPluginNodeMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PluginNodeMessageOptions>(configuration.GetSection("PluginNodeMessages"));
        services.TryAddSingleton<NodeMessageAuthentication>();
        services.AddHttpClient("node-plugin-messages").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<PluginNodeMessageService>();
        return services;
    }
}
