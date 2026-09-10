namespace LyWaf.Services.Distribution;

/// <summary>控制节点保存的目标节点连接信息；Secret 留空时继承全局 SharedSecret。</summary>
public sealed class DistributionNodeOptions
{
    public string Endpoint { get; set; } = "";
    public string Secret { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> Tags { get; set; } = [];
}

/// <summary>多节点配置分发配置；Role 可为 Standalone、Controller 或 Node。</summary>
public sealed class DistributionOptions
{
    public bool Enabled { get; set; }
    public string Role { get; set; } = "Standalone";
    public string NodeId { get; set; } = Environment.MachineName;
    public string SharedSecret { get; set; } = "";
    public string StateDirectory { get; set; } = ".lywaf-distribution";
    public long MaxConfigBytes { get; set; } = 4 * 1024 * 1024;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int AllowedClockSkewSeconds { get; set; } = 60;
    public Dictionary<string, DistributionNodeOptions> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DistributionPermissionOptions Permissions { get; set; } = new();
}

/// <summary>限制管理用户可执行的全局和指定节点操作；全部留空时保持单管理员兼容模式。</summary>
public sealed class DistributionPermissionOptions
{
    /// <summary>允许操作所有节点和分发任务的用户名。</summary>
    public List<string> GlobalOperators { get; set; } = [];
    /// <summary>每个节点允许的用户名；全局操作员自动拥有所有节点权限。</summary>
    public Dictionary<string, List<string>> NodeOperators { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>控制节点创建分发任务时使用的灰度批次参数。</summary>
public sealed record CreateDistributionRequest(
    [property: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200)] string VersionId,
    [property: System.ComponentModel.DataAnnotations.MaxLength(100)] IReadOnlyList<string>? NodeIds = null,
    [property: System.ComponentModel.DataAnnotations.Range(1, 100)] int CanaryCount = 1,
    [property: System.ComponentModel.DataAnnotations.Range(1, 100)] int BatchSize = 10,
    bool PauseAfterEachBatch = true);

/// <summary>控制节点发送给目标节点的候选配置，其中内容摘要用于完整性校验。</summary>
public sealed record PrepareConfigurationRequest(
    string DeploymentId,
    string VersionId,
    string Sha256,
    string Content);

/// <summary>提交、撤销或回滚目标节点候选配置的请求。</summary>
public sealed record DistributionCommandRequest(string DeploymentId);

/// <summary>节点对 prepare/commit/rollback 返回的明确 ACK 或 NACK。</summary>
public sealed record NodeAcknowledgement(
    bool Ack,
    string NodeId,
    string DeploymentId,
    string Stage,
    string? ConfigVersionId = null,
    string? Error = null);

/// <summary>单个目标节点在分发任务中的最新状态和错误。</summary>
public sealed record DistributionTargetState(
    string NodeId,
    string State,
    DateTimeOffset UpdatedAt,
    string? ConfigVersionId = null,
    string? Error = null);

/// <summary>控制节点持久化的分发任务，包含灰度进度、暂停状态和逐节点 ACK/NACK。</summary>
public sealed record ConfigurationDeployment(
    string Id,
    string VersionId,
    string Sha256,
    DateTimeOffset CreatedAt,
    string State,
    int CanaryCount,
    int BatchSize,
    bool PauseAfterEachBatch,
    int NextIndex,
    IReadOnlyList<string> NodeOrder,
    IReadOnlyDictionary<string, DistributionTargetState> Targets,
    string? TaskId = null);

/// <summary>目标节点公开的运行版本与最近分发状态。</summary>
public sealed record DistributionNodeStatus(
    string NodeId,
    string Role,
    string ApplicationVersion,
    string? LastDeploymentId,
    string? ConfigVersionId,
    string State,
    DateTimeOffset Timestamp);

/// <summary>节点上单个 YARP 上游目标的主动、被动健康及当前并发请求快照。</summary>
public sealed record NodeUpstreamSnapshot(
    string ClusterId,
    string DestinationId,
    string Address,
    string ActiveHealth,
    string PassiveHealth,
    int ConcurrentRequests);

/// <summary>节点安全事件累计计数的紧凑快照，用于多节点总览聚合。</summary>
public sealed record NodeSecuritySnapshot(
    long TotalInterceptCount,
    long WafInterceptCount,
    long BlacklistBlockCount,
    long CcAttackCount,
    long CrawlerDetectCount,
    long GeoBlockCount,
    long RateLimitCount,
    int UniqueAttackIps);

/// <summary>节点最近一次聚合拦截事件；EventId 在事件未变化时保持稳定，便于控制端去重。</summary>
public sealed record NodeSecurityEvent(
    string EventId,
    string SourceIp,
    string Application,
    string RuleName,
    string RuleType,
    long HitCount,
    DateTimeOffset LastHitAt);

/// <summary>节点 agent 返回的完整控制台快照，包含版本、运行状态、上游健康和安全摘要。</summary>
public sealed record NodeDashboardSnapshot(
    string NodeId,
    string ApplicationVersion,
    string? ConfigVersionId,
    string State,
    DateTimeOffset Timestamp,
    long UptimeSeconds,
    int RuntimeStateEntries,
    NodeSecuritySnapshot Security,
    IReadOnlyList<NodeUpstreamSnapshot> Upstreams,
    IReadOnlyList<NodeSecurityEvent> RecentEvents);

/// <summary>控制节点包装后的节点视图，保留离线节点最后快照并标记网络和时钟异常。</summary>
public sealed record ManagedNodeSnapshot(
    string NodeId,
    string Endpoint,
    IReadOnlyList<string> Tags,
    bool Online,
    bool Stale,
    double? ClockSkewSeconds,
    bool ClockSkewWarning,
    DateTimeOffset? LastSeenAt,
    string? Error,
    bool CanOperate,
    NodeDashboardSnapshot? Snapshot);

/// <summary>多节点控制台一次查询的聚合结果和当前用户操作权限。</summary>
public sealed record MultiNodeDashboard(
    DateTimeOffset GeneratedAt,
    bool CanOperateGlobally,
    IReadOnlyList<ManagedNodeSnapshot> Nodes,
    NodeSecuritySnapshot Security,
    IReadOnlyList<NodeSecurityEvent> RecentEvents);
