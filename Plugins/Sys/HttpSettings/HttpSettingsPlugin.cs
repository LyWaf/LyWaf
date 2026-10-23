using LyWaf.Plugins.Core;

namespace LyWaf.Plugins.Sys.HttpSettings;

/// <summary>Provides common HTTP policies at the first proxy stage.</summary>
public class HttpSettingsPlugin : LyWafPluginBase, IPluginProxyStages
{
    private readonly PluginMetadata metadata = new()
    {
        Id = "http-settings", LocalizationResourcePrefix = "LyWaf.Plugins.Sys.HttpSettings.Localization",
        Name = "http通用设置", Version = "1.1.0", Author = "LyWaf Team",
        Description = "统一处理请求头、响应头、安全响应头与请求标识，支持站点和路径配置",
        Priority = PluginPriority.Highest, EnabledByDefault = false,
        SupportsScopedConfiguration = true, DefaultOptions = new HttpSettingsOptions()
    };
    public override PluginMetadata Metadata => metadata;

    public void ConfigureProxyStage(IApplicationBuilder app, PluginProxyStage stage)
    {
        if (stage != PluginProxyStage.First) return;
        app.Use(next => new HttpSettingsMiddleware(next, context =>
            context.RequestServices.GetService<IPluginRequestConfiguration>()?.Get<HttpSettingsOptions>(context, metadata.Id)
            ?? (HttpSettingsOptions)metadata.DefaultOptions!).InvokeAsync);
    }
}
