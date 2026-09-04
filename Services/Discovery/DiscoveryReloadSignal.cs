using Microsoft.Extensions.Configuration;

namespace LyWaf.Services.Discovery;

/// <summary>通过 IConfiguration reload token 通知 YARP 重新运行配置过滤器，不向业务配置注入数据。</summary>
public sealed class DiscoveryReloadSignal : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public void Pulse(string version)
    {
        Data["ServiceDiscovery:RuntimeVersion"] = version;
        OnReload();
    }
}
