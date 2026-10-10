using LyWaf.Plugins.Core;

namespace LyWaf.Plugins.Sys.HttpUpload;

/// <summary>Provides HMAC-authenticated, bounded uploads to explicitly configured directories.</summary>
public sealed class HttpUploadPlugin : LyWafPluginBase, IPluginProxyStages
{
    public override PluginMetadata Metadata => new()
    {
        Id = "http-upload", Name = "HTTP 上传", Version = "1.0.0",
        Description = "通过 HMAC-SHA256 签名上传文件，可选安全解压 ZIP", EnabledByDefault = false,
        RequestedPermissions = PluginPermission.DataDirectory | PluginPermission.SensitiveConfiguration,
        DefaultOptions = new HttpUploadOptions(),
    };

    public override void ConfigureProxyPipeline(IApplicationBuilder app) => app.UseMiddleware<HttpUploadMiddleware>();

    public void ConfigureProxyStage(IApplicationBuilder app, PluginProxyStage stage)
    {
        if (stage == PluginProxyStage.First)
            app.UseMiddleware<UploadRequestLimitMiddleware>();
    }
}
