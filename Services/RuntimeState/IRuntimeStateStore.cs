namespace LyWaf.Services.RuntimeState;

/// <summary>
/// 为安全规则和插件提供低延迟本地状态，并在后台完成持久化和可选节点复制。
/// 请求热路径不得依赖远程节点是否可用。
/// </summary>
public interface IRuntimeStateStore
{
    /// <summary>写入带可选有效期的普通状态。</summary>
    void Set<T>(string domain, string key, T value, TimeSpan? timeToLive = null);
    /// <summary>读取未过期、未删除的普通状态。</summary>
    bool TryGet<T>(string domain, string key, out T? value);
    /// <summary>原子读取并删除普通状态，适合一次性 token；删除墓碑会在后台传播。</summary>
    bool TryTake<T>(string domain, string key, out T? value);
    /// <summary>写入删除墓碑，使删除操作也能传播到其他节点。</summary>
    bool Remove(string domain, string key);
    /// <summary>增加当前节点在固定时间窗口内的计数，并返回所有已知节点分量之和。</summary>
    long IncrementWindow(string domain, string key, DateTimeOffset windowStart, TimeSpan windowLength, long amount = 1);
    /// <summary>读取指定固定窗口的所有已知节点计数总和。</summary>
    long GetWindowValue(string domain, string key, DateTimeOffset windowStart);
    /// <summary>尝试在当前节点原子占用一个幂等键；已存在且未过期时返回 false。</summary>
    bool TryClaim(string domain, string key, TimeSpan timeToLive);
    /// <summary>合并来自持久化日志或其他节点的状态。</summary>
    bool Merge(RuntimeStateMutation mutation, bool replicate = false);
    /// <summary>返回诊断摘要，不暴露可能包含敏感信息的正文。</summary>
    IReadOnlyList<RuntimeStateSnapshot> GetSnapshots(string? domain = null);
    /// <summary>枚举指定域内可反序列化的活动值，供需要聚合状态的内部服务使用。</summary>
    IReadOnlyList<(string Key, T Value)> GetValues<T>(string domain);
    /// <summary>返回当前存储和后台队列状态。</summary>
    RuntimeStateStatus GetStatus();
}
