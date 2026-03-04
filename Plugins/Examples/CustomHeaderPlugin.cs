using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;

namespace LyWaf.Plugins.Examples;

/// <summary>
/// 示例插件：自定义响应头
/// 演示如何创建一个简单的中间件插件
/// </summary>
public class CustomHeaderPlugin : LyWafPluginBase
{
    private readonly PluginMetadata _metadata = new()
    {
        Id = "custom-header",
        Name = "自定义响应头",
        Version = "1.0.0",
        Description = "为所有响应添加自定义 HTTP 头",
        Author = "LyWaf Team",
        Priority = PluginPriority.Low,  // 低优先级，在其他处理之后
        EnabledByDefault = false,  // 默认禁用
        DefaultOptions = new CustomHeaderOptions()
    };

    public override PluginMetadata Metadata => _metadata;

    /// <summary>运行时配置，与 Metadata.DefaultOptions 是同一实例</summary>
    private CustomHeaderOptions Options => (CustomHeaderOptions)_metadata.DefaultOptions!;

    public override Task InitializeAsync(IPluginContext context)
    {
        base.InitializeAsync(context);
        context.Logger.Info("自定义响应头插件已初始化，共 {Count} 个头", Options.Headers.Count);
        return Task.CompletedTask;
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        if (Options.Enabled && Options.Headers.Count > 0)
        {
            proxyApp.Use(async (context, next) =>
            {
                // 在响应开始前注册回调
                context.Response.OnStarting(() =>
                {
                    foreach (var (key, value) in Options.Headers)
                    {
                        if (!context.Response.Headers.ContainsKey(key))
                        {
                            context.Response.Headers.Append(key, value);
                        }
                    }
                    return Task.CompletedTask;
                });

                await next(context);
            });
        }
    }
}

/// <summary>
/// 自定义响应头配置
/// </summary>
public class CustomHeaderOptions
{
    /// <summary>是否启用</summary>
    [System.ComponentModel.Description("启用")]
    public bool Enabled { get; set; } = true;

    /// <summary>要添加的响应头</summary>
    [System.ComponentModel.Description("响应头")]
    public Dictionary<string, string> Headers { get; set; } = new()
    {
        ["X-Powered-By"] = "LyWaf",
        ["X-Frame-Options"] = "SAMEORIGIN"
    };
}
