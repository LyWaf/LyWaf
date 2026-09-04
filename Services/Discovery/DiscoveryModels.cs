using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Services.Discovery;

/// <summary>
/// 表示服务发现 Provider 返回的单个上游实例。
/// 协调器会把该模型转换为 YARP Destination，因此 <paramref name="Address"/> 必须是带协议的绝对 URI。
/// </summary>
/// <param name="Id">实例在所属 Cluster 内稳定且唯一的标识；用于识别配置是否发生变化。</param>
/// <param name="Address">YARP 转发请求使用的绝对地址，例如 <c>http://10.0.0.8:8080/</c>。</param>
/// <param name="Metadata">Provider 附带的只读元数据，例如 DNS 主机名、TTL、SRV 优先级和权重。</param>
public sealed record DiscoveredDestination(
    string Id,
    string Address,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// 表示一个 Cluster 最近一次成功发布的不可变服务发现结果。
/// 注册表使用 <paramref name="Version"/> 判断实例集合是否变化，避免无变化时重复重建 YARP 配置。
/// </summary>
/// <param name="ClusterId">该快照要更新的 YARP Cluster ID。</param>
/// <param name="ProviderId">生成该快照的一个或多个 Provider ID。</param>
/// <param name="Version">根据排序后的实例及元数据计算出的稳定内容摘要。</param>
/// <param name="ObservedAt">协调器确认并发布该结果的 UTC 时间。</param>
/// <param name="Destinations">本次发现到的完整上游实例集合。</param>
public sealed record DiscoverySnapshot(
    string ClusterId,
    string ProviderId,
    string Version,
    DateTimeOffset ObservedAt,
    IReadOnlyList<DiscoveredDestination> Destinations);

/// <summary>单个服务发现 Provider 的配置；不同 Type 使用其对应字段。</summary>
public sealed class DiscoveryProviderOptions
{
    /// <summary>Provider 类型：File、Dns、Docker 或 Kubernetes。</summary>
    public string Type { get; set; } = "File";
    /// <summary>需要替换 Destinations 的 YARP Cluster ID。</summary>
    public string ClusterId { get; set; } = "";
    /// <summary>轮询间隔秒数。</summary>
    public int RefreshSeconds { get; set; } = 30;
    /// <summary>文件 Provider 的 JSON 文件路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>DNS Provider 查询的主机名。</summary>
    public string Host { get; set; } = "";
    /// <summary>DNS Provider 生成上游 URI 时使用的端口。</summary>
    public int Port { get; set; } = 80;
    /// <summary>生成上游 URI 时使用的协议。</summary>
    public string Scheme { get; set; } = "http";
    /// <summary>是否接收 IPv4 地址。</summary>
    public bool IncludeIPv4 { get; set; } = true;
    /// <summary>是否接收 IPv6 地址。</summary>
    public bool IncludeIPv6 { get; set; } = true;
    /// <summary>DNS 查询模式：Address 查询 A/AAAA，SRV 查询服务记录。</summary>
    public string RecordType { get; set; } = "Address";
    /// <summary>SRV 查询使用的 DNS 服务器；留空时选取系统网卡 DNS。</summary>
    public string DnsServer { get; set; } = "";
    /// <summary>DNS TTL 驱动刷新时允许的最短间隔秒数。</summary>
    public int DnsMinTtlSeconds { get; set; } = 5;
    /// <summary>DNS TTL 驱动刷新时允许的最长间隔秒数。</summary>
    public int DnsMaxTtlSeconds { get; set; } = 300;
    /// <summary>远程 Provider 的 API 地址。</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>Docker 标签或 Kubernetes 标签选择器。</summary>
    public string Selector { get; set; } = "";
    /// <summary>Kubernetes 命名空间。</summary>
    public string Namespace { get; set; } = "default";
    /// <summary>Kubernetes Service 名称。</summary>
    public string Service { get; set; } = "";
    /// <summary>Kubernetes 入口资源类型：Service、Ingress 或 GatewayApi。</summary>
    public string ResourceKind { get; set; } = "Service";
    /// <summary>Ingress 或 Gateway API HTTPRoute 的资源名称。</summary>
    public string ResourceName { get; set; } = "";
    /// <summary>远程 API Bearer Token 文件。</summary>
    public string TokenFile { get; set; } = "";
    /// <summary>Kubernetes API 自签根证书文件；留空时自动使用 ServiceAccount ca.crt。</summary>
    public string CaFile { get; set; } = "";
    /// <summary>文件或远程 API 单次响应允许的最大字节数。</summary>
    public long MaxPayloadBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>服务发现总配置，Providers 的键是稳定 Provider ID。</summary>
public sealed class ServiceDiscoveryOptions
{
    /// <summary>是否启动服务发现后台协调器；关闭后将清除动态发现快照。</summary>
    public bool Enabled { get; set; }
    /// <summary>连续变化稳定多久后才发布，避免频繁重建代理配置。</summary>
    public int DebounceMilliseconds { get; set; } = 500;
    /// <summary>是否允许发布空实例集；默认保留最近一份非空有效快照。</summary>
    public bool AllowEmpty { get; set; }
    /// <summary>以 Provider ID 为键的发现配置集合；键在配置生命周期内应保持稳定。</summary>
    public Dictionary<string, DiscoveryProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>服务发现实现统一接口；异常表示本次失败，协调器会保留最近有效快照。</summary>
public interface IServiceDiscoveryProvider
{
    /// <summary>Provider 类型名称，用于匹配配置中的 <see cref="DiscoveryProviderOptions.Type"/>。</summary>
    string Type { get; }

    /// <summary>执行一次完整发现；失败时应抛出异常，由协调器保留最近一次有效结果。</summary>
    /// <param name="providerId">当前 Provider 的稳定配置 ID，用于诊断错误。</param>
    /// <param name="options">当前 Provider 配置快照。</param>
    /// <param name="cancellationToken">应用停止或本次发现取消信号。</param>
    /// <returns>本次发现到的完整实例集合，而不是相对上一轮的增量。</returns>
    Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(
        string providerId,
        DiscoveryProviderOptions options,
        CancellationToken cancellationToken);
}

/// <summary>为排序后的实例集合生成稳定 SHA-256 版本号。</summary>
public static class DiscoveryVersion
{
    /// <summary>根据实例 ID、地址和排序后的元数据生成稳定的小写 SHA-256 摘要。</summary>
    /// <param name="destinations">需要参与版本计算的完整实例集合。</param>
    /// <returns>64 个十六进制字符组成的内容版本。</returns>
    public static string Create(IEnumerable<DiscoveredDestination> destinations)
    {
        var canonical = string.Join("\n", destinations
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => $"{x.Id}\t{x.Address}\t{string.Join(';', (x.Metadata ?? new Dictionary<string, string>()).OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
