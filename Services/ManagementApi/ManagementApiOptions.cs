namespace LyWaf.Services.ManagementApi;

/// <summary>管理 API 的统一请求大小、频率和审计配置。</summary>
public sealed class ManagementApiOptions
{
    /// <summary>是否允许控制端监听非回环地址；开启后仍必须部署 TLS 和网络访问控制。</summary>
    public bool AllowRemoteControl { get; set; }

    /// <summary>单个管理 API 请求体最大字节数。</summary>
    public long MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>单个来源每分钟最多读取请求数。</summary>
    public int ReadRequestsPerMinute { get; set; } = 600;

    /// <summary>单个来源每分钟最多写请求数。</summary>
    public int WriteRequestsPerMinute { get; set; } = 120;

    /// <summary>内存中最多跟踪的来源数，超出后归入共享溢出桶。</summary>
    public int MaxTrackedClients { get; set; } = 10_000;

    /// <summary>是否记录每个写请求的方法、规范化路径、状态码和请求 ID。</summary>
    public bool AuditMutatingRequests { get; set; } = true;
}
