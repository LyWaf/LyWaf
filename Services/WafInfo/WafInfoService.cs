
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using LyWaf.Services.Acme;
using LyWaf.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.WafInfo;

public interface IWafInfoService
{
    public WafInfoOptions GetOptions(); 
    public X509Certificate2 GetCertByName(string name);
}


public class WafInfoService : IWafInfoService
{
    private WafInfoOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WafInfoService> _logger;
    private readonly IAcmeService? _acmeService;

    private readonly Dictionary<string, X509Certificate2> certs = [];
    public WafInfoService(
        IOptionsMonitor<WafInfoOptions> options, IMemoryCache cache,
        ILogger<WafInfoService> logger,
        IAcmeService? acmeService = null)
    {
        _options = options.CurrentValue;
        _acmeService = acmeService;
        // 可以订阅变更，但需注意生命周期和内存泄漏
        options.OnChange(newConfig =>
        {
            _options = newConfig;
            BuildCerts();
        });
        BuildCerts();
        _cache = cache;
        _logger = logger;
    }

    public WafInfoOptions GetOptions()
    {
        return _options;
    }

    public void BuildCerts()
    {
        foreach (var url in _options.Certs)
        {
            X509Certificate2 cert = CertUtils.LoadFromFile(url.PemFile, url.KeyFile);
            certs.Remove(url.Host);
            certs.Add(url.Host, cert);
        }
    }

    public X509Certificate2 GetCertByName(string name)
    {
        // 首先尝试从 ACME 服务获取证书
        if (_acmeService != null)
        {
            var acmeCert = _acmeService.GetCertificate(name);
            if (acmeCert != null)
            {
                return acmeCert;
            }
        }

        // 然后从配置的证书中查找
        switch (certs.Count)
        {
            case 0:
                {
                    throw new Exception("不存在任何证书配置");
                }
            case 1:
                {
                    return certs.First().Value;
                }
            default:
                {
                    foreach (var (k, v) in certs)
                    {
                        if (k == name)
                        {
                            return v;
                        }
                        if (k.StartsWith("*.") && name.EndsWith(k[2..]))
                        {
                            return v;
                        }
                    }
                    return certs.First().Value;
                }
        }
    }
}