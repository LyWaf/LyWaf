using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.Discovery;

/// <summary>轮询所有 Provider、合并同 Cluster 结果、去抖发布，并在失败时保留最近有效状态。</summary>
public sealed class DiscoveryCoordinator(
    IOptionsMonitor<ServiceDiscoveryOptions> options,
    IEnumerable<IServiceDiscoveryProvider> providers,
    DiscoveryRegistry registry,
    DiscoveryReloadSignal reloadSignal) : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, DateTimeOffset> _nextRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<DiscoveredDestination>> _providerResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Version, DateTimeOffset FirstSeen)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _wasEnabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var current = options.CurrentValue;
                if (current.Enabled)
                {
                    _wasEnabled = true;
                    await PollAsync(current, stoppingToken);
                }
                else if (_wasEnabled)
                {
                    _wasEnabled = false;
                    if (registry.Clear()) reloadSignal.Pulse("disabled-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                await LyWaf.Services.Lifecycle.ShutdownWait.DelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task PollAsync(ServiceDiscoveryOptions current, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var implementations = providers.ToDictionary(x => x.Type, StringComparer.OrdinalIgnoreCase);
        var changedClusters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredClusters = current.Providers.Values.Select(x => x.ClusterId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in registry.GetAll().Where(x => !configuredClusters.Contains(x.ClusterId)))
        {
            if (registry.Remove(stale.ClusterId)) reloadSignal.Pulse("removed-" + stale.ClusterId + "-" + now.ToUnixTimeMilliseconds());
        }
        foreach (var (providerId, providerOptions) in current.Providers)
        {
            if (_nextRuns.TryGetValue(providerId, out var next) && next > now) continue;
            _nextRuns[providerId] = now.AddSeconds(Math.Clamp(providerOptions.RefreshSeconds, 1, 3600));
            try
            {
                if (string.IsNullOrWhiteSpace(providerOptions.ClusterId)) throw new InvalidOperationException("ClusterId 不能为空");
                if (!implementations.TryGetValue(providerOptions.Type, out var implementation))
                    throw new InvalidOperationException($"不支持的 Provider 类型: {providerOptions.Type}");
                var result = await implementation.DiscoverAsync(providerId, providerOptions, cancellationToken);
                if (result.Count == 0 && !current.AllowEmpty) throw new InvalidOperationException("发现结果为空");
                var ttlValues = result.Select(item => item.Metadata?.GetValueOrDefault("dns.ttl_seconds"))
                    .Where(static value => int.TryParse(value, out _))
                    .Select(static value => int.Parse(value!))
                    .ToArray();
                if (ttlValues.Length > 0)
                {
                    var minimumTtl = Math.Clamp(providerOptions.DnsMinTtlSeconds, 1, 3600);
                    var maximumTtl = Math.Clamp(providerOptions.DnsMaxTtlSeconds, minimumTtl, 86400);
                    _nextRuns[providerId] = now.AddSeconds(Math.Clamp(ttlValues.Min(), minimumTtl, maximumTtl));
                }
                _providerResults[providerId] = result;
                changedClusters.Add(providerOptions.ClusterId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn(ex, "服务发现失败，保留最近有效状态: Provider={ProviderId}, Cluster={ClusterId}", providerId, providerOptions.ClusterId);
            }
        }

        changedClusters.UnionWith(_pending.Keys);
        foreach (var clusterId in changedClusters)
        {
            var candidates = current.Providers
                .Where(x => x.Value.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => _providerResults.GetValueOrDefault(x.Key) ?? [])
                .ToArray();
            var duplicate = candidates.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null)
            {
                Logger.Warn("服务发现候选包含重复 Destination ID，保留最近有效状态: Cluster={ClusterId}, Id={DestinationId}", clusterId, duplicate.Key);
                continue;
            }
            var destinations = candidates.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            if (destinations.Length == 0 && !current.AllowEmpty) continue;
            var version = DiscoveryVersion.Create(destinations);
            if (!_pending.TryGetValue(clusterId, out var pending) || pending.Version != version)
            {
                _pending[clusterId] = (version, now);
                if (current.DebounceMilliseconds > 0) continue;
            }
            else if (now - pending.FirstSeen < TimeSpan.FromMilliseconds(Math.Max(0, current.DebounceMilliseconds))) continue;

            var providerIds = string.Join(',', current.Providers.Where(x => x.Value.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key));
            if (registry.Publish(new DiscoverySnapshot(clusterId, providerIds, version, now, destinations)))
            {
                Logger.Info("发布服务发现快照: Cluster={ClusterId}, Version={Version}, Destinations={Count}", clusterId, version[..12], destinations.Length);
                reloadSignal.Pulse(version);
            }
            _pending.Remove(clusterId);
        }
    }
}
