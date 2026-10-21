using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

/// <summary>
/// 控制运行状态的本地持久化和节点复制。Standalone 模式不依赖任何外部服务。
/// </summary>
public sealed class RuntimeStateOptions
{
    internal string ManagedTopologyRole { get; set; } = "";
    internal string HubControllerNodeId { get; set; } = "";
    internal Func<string, bool>? HubSourceIsKnown { get; set; }
    /// <summary>Local key namespaces bound to protection groups; never trust a group label supplied by a peer.</summary>
    public Dictionary<string, List<string>> ProtectionGroupKeyPrefixes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Cluster-specific domain bound into end-to-end CC release signatures.</summary>
    public string CcReleaseTrustDomain { get; set; } = "";
    /// <summary>Authorized v2 peer used for scoped captcha release when this node has no signing key.</summary>
    public string CcCaptchaAuthorityNodeId { get; set; } = "";
    /// <summary>Node-local ECDSA P-256 private PEM. Configure only on authorized release issuers; never distribute to ordinary peers.</summary>
    public string CcReleasePrivateKeyPem { get; set; } = "";
    /// <summary>Trusted issuer NodeId to public PEM. Transport HMAC grants no release authority.</summary>
    public Dictionary<string, string> CcReleaseAuthorities { get; set; } = new(StringComparer.Ordinal);
    /// <summary>是否启用跨节点复制；本地状态存储始终可用。</summary>
    public bool Enabled { get; set; }
    /// <summary>当前节点的稳定标识，用于生成可比较版本和计数器分量。</summary>
    public string NodeId { get; set; } = Environment.MachineName;
    /// <summary>流式状态日志目录。</summary>
    public string StateDirectory { get; set; } = ".lywaf-runtime-state";
    /// <summary>后台持久化与复制共用队列的最大待处理数量。</summary>
    public int QueueCapacity { get; set; } = 10_000;
    /// <summary>Total queued and in-flight replication entries across ingress and every peer. Restart to resize.</summary>
    public int ReplicationQueueMaxEntries { get; set; } = 20_000;
    /// <summary>Total serialized mutation bytes charged across all replication queues and active batches.</summary>
    public long ReplicationQueueMaxBytes { get; set; } = 64L * 1024 * 1024;
    /// <summary>Per-queue byte ceiling; prevents one slow peer from using the entire global budget.</summary>
    public long ReplicationQueuePerQueueMaxBytes { get; set; } = 8L * 1024 * 1024;
    /// <summary>Maximum serialized mutation size, including identity, counter components and proof.</summary>
    public int ReplicationMaxMutationBytes { get; set; } = 4 * 1024 * 1024;
    /// <summary>单条状态值允许的最大 UTF-8 字节数。</summary>
    public int MaxValueBytes { get; set; } = 64 * 1024;
    /// <summary>Compaction trigger, not a hard limit. MaxJournalDiskBytes bounds actual journal growth.</summary>
    public long MaxJournalBytes { get; set; } = 128L * 1024 * 1024;
    public int MaxStateEntries { get; set; } = 100_000;
    public long MaxStateBytes { get; set; } = 128L * 1024 * 1024;
    public long MaxJournalQueueBytes { get; set; } = 32L * 1024 * 1024;
    public long MaxJournalDiskBytes { get; set; } = 256L * 1024 * 1024;
    public long MaxDiskBytes { get; set; } = 512L * 1024 * 1024;
    public int MaxDiskFiles { get; set; } = 100_000;
    public int MaxExpiredVersionEntries { get; set; } = 100_000;
    public long MaxExpiredVersionBytes { get; set; } = 32L * 1024 * 1024;
    /// <summary>节点间请求允许的最大时钟偏差。</summary>
    public int AllowedClockSkewSeconds { get; set; } = 60;
    /// <summary>节点复制使用的共享 HMAC 密钥。</summary>
    public string SharedSecret { get; set; } = "";
    /// <summary>需要异步复制状态的对等节点。</summary>
    public Dictionary<string, RuntimeStatePeerOptions> Peers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>描述一个运行状态对等节点；Secret 留空时使用全局 SharedSecret。</summary>
public sealed class RuntimeStatePeerOptions
{
    /// <summary>Opt-in durable runtime.sync stream; requires v2 and explicit operation authorization on both peers.</summary>
    public bool DurableSyncEnabled { get; set; }
    public bool BootstrapAuthority { get; set; }
    public bool AllowBootstrapExport { get; set; }
    public int SyncOfflineWindowSeconds { get; set; } = 24 * 60 * 60;
    public RuntimeStateReplicationPolicy Send { get; set; } = new();
    public RuntimeStateReplicationPolicy Receive { get; set; } = new();
    public int ProtocolVersion { get; set; } = 1;
    /// <summary>目标 LyWaf 控制端地址。</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>该节点专用 HMAC 密钥。</summary>
    public string Secret { get; set; } = "";
    /// <summary>是否向该节点复制状态。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Exact, case-sensitive allowlists. Empty policies deny replication, including legacy traffic.</summary>
public sealed class RuntimeStateReplicationPolicy
{
    public List<string> AllowedDomains { get; set; } = [];
    public List<string> AllowedProtectionGroups { get; set; } = [];
    public bool AllowUngroupedKeys { get; set; }
    public List<string> TrustedForwardedSources { get; set; } = [];
}

/// <summary>
/// 可跨节点传播的单条状态变更。普通值按 (Sequence, NodeId) 后写胜出；CounterComponents 按节点取最大值后求和。
/// </summary>
public sealed record RuntimeStateMutation(
    string Domain,
    string Key,
    string NodeId,
    long Sequence,
    DateTimeOffset ChangedAt,
    DateTimeOffset? ExpiresAt,
    bool Deleted,
    JsonElement? Value,
    IReadOnlyDictionary<string, long>? CounterComponents = null,
    CcReleaseProof? CcReleaseProof = null);

/// <summary>供诊断 API 展示的脱敏运行状态摘要，不包含状态正文。</summary>
public sealed record RuntimeStateSnapshot(
    string Domain,
    string Key,
    string NodeId,
    long Sequence,
    DateTimeOffset ChangedAt,
    DateTimeOffset? ExpiresAt,
    bool Deleted,
    long? CounterValue);

/// <summary>运行状态存储的容量、持久化及复制队列概况。</summary>
public sealed record RuntimeStateStatus(
    string NodeId,
    int ActiveEntries,
    long Sequence,
    long PersistedMutations,
    long DroppedMutations,
    int QueueDepth,
    long StateBytes = 0,
    int RetainedEntries = 0,
    long JournalQueueBytes = 0,
    string? CapacityError = null,
    bool PersistenceReadOnly = false);
