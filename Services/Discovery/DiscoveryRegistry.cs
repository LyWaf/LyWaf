using System.Collections.Concurrent;

namespace LyWaf.Services.Discovery;

/// <summary>保存每个 Cluster 最近一次已发布的有效发现快照，供 YARP 配置过滤器无锁读取。</summary>
public sealed class DiscoveryRegistry
{
    private readonly ConcurrentDictionary<string, DiscoverySnapshot> _clusters = new(StringComparer.OrdinalIgnoreCase);

    public DiscoverySnapshot? Get(string clusterId) =>
        _clusters.TryGetValue(clusterId, out var snapshot) ? snapshot : null;

    public IReadOnlyList<DiscoverySnapshot> GetAll() =>
        _clusters.Values.OrderBy(x => x.ClusterId, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool Publish(DiscoverySnapshot snapshot)
    {
        while (true)
        {
            if (_clusters.TryGetValue(snapshot.ClusterId, out var current))
            {
                if (current.Version == snapshot.Version) return false;
                if (_clusters.TryUpdate(snapshot.ClusterId, snapshot, current)) return true;
                continue;
            }
            if (_clusters.TryAdd(snapshot.ClusterId, snapshot)) return true;
        }
    }

    public bool Remove(string clusterId) => _clusters.TryRemove(clusterId, out _);

    public bool Clear()
    {
        if (_clusters.IsEmpty) return false;
        _clusters.Clear();
        return true;
    }
}
