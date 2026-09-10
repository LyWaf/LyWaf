using System.ComponentModel.DataAnnotations;
using System.Net;

namespace LyWaf.Control;

/// <summary>新增监听请求；省略 Host 时监听所有 IPv4 地址。</summary>
public sealed class AddListenerRequest : IValidatableObject
{
    [Required, StringLength(253)]
    public string Host { get; set; } = "0.0.0.0";
    [Range(1, 65535)]
    public int Port { get; set; }
    public bool IsHttps { get; set; }
    [Range(1, 65535)]
    public int? AutoHttpsPort { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Host is not ("*" or "+") && !IPAddress.TryParse(Host, out _) && Uri.CheckHostName(Host ?? "") != UriHostNameType.Dns)
            yield return new ValidationResult("监听 Host 必须为 IP、域名或通配符", [nameof(Host)]);
    }
}

/// <summary>按端口与可选 Host 删除补丁监听，不修改原始配置。</summary>
public sealed class RemoveListenerRequest
{
    [StringLength(253)]
    public string Host { get; set; } = "";
    [Range(1, 65535)]
    public int Port { get; set; }
}
