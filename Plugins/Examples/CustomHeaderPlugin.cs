using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Examples;

/// <summary>
/// 示例插件：自定义响应头
/// 演示如何创建一个简单的中间件插件
/// </summary>
public class CustomHeaderPlugin : LyWafPluginBase
{
    private CustomHeaderOptions _options = new();
    
    public override PluginMetadata Metadata => new()
    {
        Id = "custom-header",
        Name = "自定义响应头",
        Version = "1.0.0",
        Description = "为所有响应添加自定义 HTTP 头",
        Author = "LyWaf Team",
        Priority = PluginPriority.Low,  // 低优先级，在其他处理之后
        EnabledByDefault = false  // 默认禁用
    };
    
    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CustomHeaderOptions>(configuration.GetSection("Plugins:custom-header"));
    }
    
    public override Task InitializeAsync(IPluginContext context)
    {
        _options = context.GetPluginConfig<CustomHeaderOptions>();
        context.Logger.Info("自定义响应头插件已初始化，共 {Count} 个头", _options.Headers.Count);
        return base.InitializeAsync(context);
    }
    
    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        if (_options.Enabled && _options.Headers.Count > 0)
        {
            proxyApp.Use(async (context, next) =>
            {
                // 在响应开始前注册回调
                context.Response.OnStarting(() =>
                {
                    foreach (var (key, value) in _options.Headers)
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
    public bool Enabled { get; set; } = true;
    
    /// <summary>要添加的响应头</summary>
    public Dictionary<string, string> Headers { get; set; } = new()
    {
        ["X-Powered-By"] = "LyWaf",
        ["X-Frame-Options"] = "SAMEORIGIN"
    };
}
