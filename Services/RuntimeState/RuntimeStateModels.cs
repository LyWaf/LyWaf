using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

/// <summary>
/// 控制运行状态的本地持久化和节点复制。Standalone 模式不依赖任何外部服务。
/// </summary>
public sealed class RuntimeStateOptions
{
    /// <summary>是否启用跨节点复制；本地状态存储始终可用。</summary>
    public bool Enabled { get; set; }
    /// <summary>当前节点的稳定标识，用于生成可比较版本和计数器分量。</summary>
    public string NodeId { get; set; } = Environment.MachineName;
    /// <summary>流式状态日志目录。</summary>
    public string StateDirectory { get; set; } = ".lywaf-runtime-state";
    /// <summary>后台持久化与复制共用队列的最大待处理数量。</summary>
    public int QueueCapacity { get; set; } = 10_000;
    /// <summary>单条状态值允许的最大 UTF-8 字节数。</summary>
    public int MaxValueBytes { get; set; } = 64 * 1024;
    /// <summary>启动恢复时最多读取的日志字节数，防止异常文件耗尽内存。</summary>
    public long MaxJournalBytes { get; set; } = 128L * 1024 * 1024;
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
    /// <summary>目标 LyWaf 控制端地址。</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>该节点专用 HMAC 密钥。</summary>
    public string Secret { get; set; } = "";
    /// <summary>是否向该节点复制状态。</summary>
    public bool Enabled { get; set; } = true;
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
    IReadOnlyDictionary<string, long>? CounterComponents = null);

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
    int QueueDepth);
