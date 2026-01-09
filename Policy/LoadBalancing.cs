using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LyWaf.Utils;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Policy;

/// <summary>
/// 加权轮询负载均衡策略
/// Weighted Round Robin - 根据服务器权重分配请求
/// 配置方式: Destination.Metadata["Weight"] = "权重值" (默认为1)
/// </summary>
public class WeightedRoundRobinPolicy : ILoadBalancingPolicy
{
    public string Name => "WeightedRoundRobin";

    private readonly ConcurrentDictionary<string, int> _counters = new();
    private readonly ConcurrentDictionary<string, int[]> _weightedSequence = new();
    private readonly object _lock = new();

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (availableDestinations.Count == 1)
            return availableDestinations[0];

        var clusterId = cluster.ClusterId;
        
        // 生成加权序列
        var sequence = GetOrCreateWeightedSequence(clusterId, availableDestinations);
        if (sequence.Length == 0)
            return availableDestinations[0];

        // 获取当前计数器并递增
        var counter = _counters.AddOrUpdate(clusterId, 0, (_, v) => (v + 1) % sequence.Length);
        var index = sequence[counter];

        return index < availableDestinations.Count ? availableDestinations[index] : availableDestinations[0];
    }

    private int[] GetOrCreateWeightedSequence(string clusterId, IReadOnlyList<DestinationState> destinations)
    {
        // 使用 destinations 的哈希值作为缓存键的一部分，当 destinations 变化时重新生成
        var destHash = string.Join(",", destinations.Select(d => d.DestinationId));
        var cacheKey = $"{clusterId}:{destHash}";

        return _weightedSequence.GetOrAdd(cacheKey, _ =>
        {
            var weights = destinations.Select(d => GetWeight(d)).ToArray();
            var gcd = weights.Aggregate(GCD);
            var normalizedWeights = weights.Select(w => w / gcd).ToArray();
            var totalWeight = normalizedWeights.Sum();

            var sequence = new List<int>();
            for (int i = 0; i < destinations.Count; i++)
            {
                for (int j = 0; j < normalizedWeights[i]; j++)
                {
                    sequence.Add(i);
                }
            }

            return [.. sequence];
        });
    }

    private static int GetWeight(DestinationState destination)
    {
        if (destination.Model.Config.Metadata?.TryGetValue("Weight", out var weightStr) == true
            && int.TryParse(weightStr, out var weight) && weight > 0)
        {
            return weight;
        }
        return 1;
    }

    private static int GCD(int a, int b) => b == 0 ? a : GCD(b, a % b);
}

/// <summary>
/// 加权最少连接负载均衡策略
/// Weighted Least Connections - 考虑权重的连接数最少算法
/// 计算公式: 当前连接数 / 权重，选择比值最小的服务器
/// 配置方式: Destination.Metadata["Weight"] = "权重值" (默认为1)
/// </summary>
public class WeightedLeastConnectionsPolicy : ILoadBalancingPolicy
{
    public string Name => "WeightedLeastConnections";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (availableDestinations.Count == 1)
            return availableDestinations[0];

        DestinationState? bestDestination = null;
        double bestScore = double.MaxValue;

        foreach (var destination in availableDestinations)
        {
            var weight = GetWeight(destination);
            var concurrentRequests = destination.ConcurrentRequestCount;
            
            // 计算加权分数：当前连接数 / 权重
            // 分数越低越好
            var score = (double)concurrentRequests / weight;

            if (score < bestScore)
            {
                bestScore = score;
                bestDestination = destination;
            }
        }

        return bestDestination ?? availableDestinations[0];
    }

    private static int GetWeight(DestinationState destination)
    {
        if (destination.Model.Config.Metadata?.TryGetValue("Weight", out var weightStr) == true
            && int.TryParse(weightStr, out var weight) && weight > 0)
        {
            return weight;
        }
        return 1;
    }
}

/// <summary>
/// IP哈希负载均衡策略
/// IP Hash - 基于客户端IP分配，确保同一用户访问同一服务器
/// 支持通过 X-Forwarded-For 或 X-Real-IP 或者 Forwarded 头获取真实客户端IP
/// </summary>
public class IpHashPolicy : ILoadBalancingPolicy
{
    public string Name => "IpHash";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (availableDestinations.Count == 1)
            return availableDestinations[0];

        var clientIp = RequestUtil.GetClientIp(context.Request);
        if (string.IsNullOrEmpty(clientIp))
        {
            // 如果无法获取IP，随机选择
            return availableDestinations[Random.Shared.Next(availableDestinations.Count)];
        }

        var hash = GetStableHash(clientIp);
        var index = (int)(hash % (uint)availableDestinations.Count);

        return availableDestinations[index];
    }

    private static uint GetStableHash(string input)
    {
        // 使用稳定的哈希算法，确保相同输入产生相同输出
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }
}

/// <summary>
/// 通用哈希负载均衡策略
/// Generic Hash - 基于自定义变量（如URL、参数、Header）进行哈希
/// 配置方式: Cluster.Metadata["HashKey"] = "变量表达式"
/// 支持的变量:
///   - {Path} : 请求路径
///   - {Query} : 完整查询字符串
///   - {Query.xxx} : 指定查询参数
///   - {Header.xxx} : 指定请求头
///   - {Cookie.xxx} : 指定Cookie
///   - {IP} : 客户端IP
/// </summary>
public class GenericHashPolicy : ILoadBalancingPolicy
{
    public string Name => "GenericHash";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (availableDestinations.Count == 1)
            return availableDestinations[0];

        // 获取哈希键配置，默认使用路径
        var hashKeyTemplate = cluster.Model.Config.Metadata?.GetValueOrDefault("HashKey") ?? "{Path}";
        var hashKey = ResolveHashKey(context, hashKeyTemplate);

        if (string.IsNullOrEmpty(hashKey))
        {
            return availableDestinations[Random.Shared.Next(availableDestinations.Count)];
        }

        var hash = GetStableHash(hashKey);
        var index = (int)(hash % (uint)availableDestinations.Count);

        return availableDestinations[index];
    }

    private static string ResolveHashKey(HttpContext context, string template)
    {
        var result = template;

        // 替换 {Path}
        result = result.Replace("{Path}", context.Request.Path.Value ?? "");

        // 替换 {Query}
        result = result.Replace("{Query}", context.Request.QueryString.Value ?? "");

        // 替换 {IP}
        result = result.Replace("{IP}", RequestUtil.GetClientIp(context.Request));

        // 替换 {Query.xxx}
        result = ReplacePattern(result, "{Query.", "}", name => 
            context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : "");

        // 替换 {Header.xxx}
        result = ReplacePattern(result, "{Header.", "}", name => 
            context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : "");

        // 替换 {Cookie.xxx}
        result = ReplacePattern(result, "{Cookie.", "}", name => 
            context.Request.Cookies.TryGetValue(name, out var value) ? value : "");

        return result;
    }

    private static string ReplacePattern(string input, string prefix, string suffix, Func<string, string> getValue)
    {
        var result = input;
        var startIndex = 0;

        while (true)
        {
            var prefixIndex = result.IndexOf(prefix, startIndex, StringComparison.Ordinal);
            if (prefixIndex < 0) break;

            var suffixIndex = result.IndexOf(suffix, prefixIndex + prefix.Length, StringComparison.Ordinal);
            if (suffixIndex < 0) break;

            var name = result.Substring(prefixIndex + prefix.Length, suffixIndex - prefixIndex - prefix.Length);
            var value = getValue(name);
            var placeholder = prefix + name + suffix;

            result = result.Replace(placeholder, value);
            startIndex = prefixIndex + value.Length;
        }

        return result;
    }

    private static uint GetStableHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0);
    }
}

/// <summary>
/// 加权随机负载均衡策略
/// Weighted Random - 根据权重随机选择服务器
/// 权重越高，被选中的概率越大
/// 配置方式: Destination.Metadata["Weight"] = "权重值" (默认为1)
/// </summary>
public class WeightedRandomPolicy : ILoadBalancingPolicy
{
    public string Name => "WeightedRandom";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (availableDestinations.Count == 1)
            return availableDestinations[0];

        // 计算总权重
        var weights = availableDestinations.Select(d => GetWeight(d)).ToArray();
        var totalWeight = weights.Sum();

        // 生成随机数
        var random = Random.Shared.Next(1, totalWeight + 1);

        // 根据权重选择目标
        var cumulativeWeight = 0;
        for (int i = 0; i < availableDestinations.Count; i++)
        {
            cumulativeWeight += weights[i];
            if (random <= cumulativeWeight)
            {
                return availableDestinations[i];
            }
        }

        return availableDestinations[^1];
    }

    private static int GetWeight(DestinationState destination)
    {
        if (destination.Model.Config.Metadata?.TryGetValue("Weight", out var weightStr) == true
            && int.TryParse(weightStr, out var weight) && weight > 0)
        {
            return weight;
        }
        return 1;
    }
}

/// <summary>
/// 一致性哈希负载均衡策略
/// Consistent Hash - 使用一致性哈希环，当节点变化时尽量减少请求的重新分配
/// 配置方式: 
///   - Cluster.Metadata["HashKey"] = "变量表达式" (同GenericHash)
///   - Destination.Metadata["VirtualNodes"] = "虚拟节点数" (默认为150)
/// </summary>
public class ConsistentHashPolicy : ILoadBalancingPolicy
{
    public string Name => "ConsistentHash";

    private readonly ConcurrentDictionary<string, ConsistentHashRing> _rings = new();

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
            return null;

        if (availableDestinations.Count == 1)
            return availableDestinations[0];

        // 获取或创建哈希环
        var ring = GetOrCreateRing(cluster.ClusterId, availableDestinations);

        // 获取哈希键
        var hashKeyTemplate = cluster.Model.Config.Metadata?.GetValueOrDefault("HashKey") ?? "{IP}";
        var hashKey = ResolveHashKey(context, hashKeyTemplate);

        if (string.IsNullOrEmpty(hashKey))
        {
            return availableDestinations[Random.Shared.Next(availableDestinations.Count)];
        }

        return ring.GetNode(hashKey);
    }

    private ConsistentHashRing GetOrCreateRing(string clusterId, IReadOnlyList<DestinationState> destinations)
    {
        var destHash = string.Join(",", destinations.Select(d => d.DestinationId));
        var cacheKey = $"{clusterId}:{destHash}";

        return _rings.GetOrAdd(cacheKey, _ => new ConsistentHashRing(destinations));
    }

    private static string ResolveHashKey(HttpContext context, string template)
    {
        var result = template;

        result = result.Replace("{Path}", context.Request.Path.Value ?? "");
        result = result.Replace("{Query}", context.Request.QueryString.Value ?? "");
        result = result.Replace("{IP}", RequestUtil.GetClientIp(context.Request));

        result = ReplacePattern(result, "{Query.", "}", name => 
            context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : "");

        result = ReplacePattern(result, "{Header.", "}", name => 
            context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : "");

        result = ReplacePattern(result, "{Cookie.", "}", name => 
            context.Request.Cookies.TryGetValue(name, out var value) ? value : "");

        return result;
    }

    private static string ReplacePattern(string input, string prefix, string suffix, Func<string, string> getValue)
    {
        var result = input;
        var startIndex = 0;

        while (true)
        {
            var prefixIndex = result.IndexOf(prefix, startIndex, StringComparison.Ordinal);
            if (prefixIndex < 0) break;

            var suffixIndex = result.IndexOf(suffix, prefixIndex + prefix.Length, StringComparison.Ordinal);
            if (suffixIndex < 0) break;

            var name = result.Substring(prefixIndex + prefix.Length, suffixIndex - prefixIndex - prefix.Length);
            var value = getValue(name);
            var placeholder = prefix + name + suffix;

            result = result.Replace(placeholder, value);
            startIndex = prefixIndex + value.Length;
        }

        return result;
    }

    private class ConsistentHashRing
    {
        private readonly SortedDictionary<uint, DestinationState> _ring = new();
        private readonly uint[] _sortedKeys;

        public ConsistentHashRing(IReadOnlyList<DestinationState> destinations, int defaultVirtualNodes = 150)
        {
            foreach (var destination in destinations)
            {
                var virtualNodes = GetVirtualNodes(destination, defaultVirtualNodes);
                for (int i = 0; i < virtualNodes; i++)
                {
                    var key = $"{destination.DestinationId}:{i}";
                    var hash = GetHash(key);
                    _ring[hash] = destination;
                }
            }
            _sortedKeys = [.. _ring.Keys];
        }

        private static int GetVirtualNodes(DestinationState destination, int defaultValue)
        {
            if (destination.Model.Config.Metadata?.TryGetValue("VirtualNodes", out var vnStr) == true
                && int.TryParse(vnStr, out var vn) && vn > 0)
            {
                return vn;
            }
            return defaultValue;
        }

        public DestinationState GetNode(string key)
        {
            var hash = GetHash(key);
            var index = BinarySearchClosest(hash);
            return _ring[_sortedKeys[index]];
        }

        private int BinarySearchClosest(uint hash)
        {
            var left = 0;
            var right = _sortedKeys.Length - 1;

            if (hash <= _sortedKeys[left] || hash > _sortedKeys[right])
                return 0;

            while (left < right)
            {
                var mid = (left + right) / 2;
                if (_sortedKeys[mid] < hash)
                    left = mid + 1;
                else
                    right = mid;
            }

            return left;
        }

        private static uint GetHash(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = MD5.HashData(bytes);
            return BitConverter.ToUInt32(hash, 0);
        }
    }
}
