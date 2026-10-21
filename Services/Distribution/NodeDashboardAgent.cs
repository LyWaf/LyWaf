using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.NodeCommunication;
using LyWaf.Shared;
using Yarp.ReverseProxy;

namespace LyWaf.Services.Distribution;

/// <summary>从当前节点的权威运行对象构建只读控制台快照，不通过本机 HTTP 绕行。</summary>
public sealed class NodeDashboardAgent(
    DistributionNodeService distributionNode,
    IRuntimeStateStore runtimeState,
    IProxyStateLookup proxyState,
    NodeTelemetryCollector? telemetry = null,
    NodeIdentityService? identity = null,
    NodeDashboardDiagnostics? diagnostics = null,
    NodeLogicalBanTelemetry? logicalBans = null)
{
    private static readonly DateTimeOffset ProcessStartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static readonly string ProcessBootId = Guid.NewGuid().ToString("N");

    public NodeDashboardSnapshot Capture()
    {
        var node = distributionNode.GetStatus();
        var boot = telemetry?.BootId ?? ProcessBootId;
        var bootSource = "process_telemetry";
        if (identity is not null)
        {
            try { boot = identity.Capture().BootId; bootSource = "node_identity"; }
            catch (InvalidOperationException) { bootSource = "process_telemetry_identity_unavailable"; }
        }
        var security = SharedData.Security.GetSnapshot(out var attackIps);
        var upstreams = proxyState.GetClusters().SelectMany(cluster => cluster.Destinations.Values.Select(destination =>
            new NodeUpstreamSnapshot(cluster.ClusterId, destination.DestinationId,
                destination.Model.Config.Address, destination.Health.Active.ToString(),
                destination.Health.Passive.ToString(), destination.ConcurrentRequestCount))).ToArray();
        var events = SharedData.GetInterceptEvents(startTime: DateTime.UtcNow.AddHours(-24)).Take(100)
            .Select(item => new NodeSecurityEvent(CreateEventId(node.NodeId, boot, item), item.SourceIp, item.Application,
                item.RuleName, item.RuleType, Interlocked.Read(ref item.HitCount), item.LastHitTime, boot)).ToArray();
        return new NodeDashboardSnapshot(node.NodeId, node.ApplicationVersion, node.ConfigVersionId, node.State,
            DateTimeOffset.UtcNow, Math.Max(0, (long)(DateTimeOffset.UtcNow - ProcessStartedAt).TotalSeconds),
            runtimeState.GetStatus().ActiveEntries,
            new NodeSecuritySnapshot(security.TotalInterceptCount, security.WafInterceptCount,
                security.BlacklistBlockCount, security.CcAttackCount, security.CrawlerDetectCount,
                security.GeoBlockCount, security.RateLimitCount, security.UniqueAttackIps), upstreams, events, attackIps,
            new DateTimeOffset(security.StartTime.ToUniversalTime()), telemetry?.Capture(), boot, bootSource,
            diagnostics?.CaptureLocal(), logicalBans?.Capture(boot));
    }

    private static string CreateEventId(string nodeId, string bootId, InterceptEvent item)
    {
        var value = $"{nodeId}\n{bootId}\n{item.SourceIp}\n{item.Application}\n{item.RuleName}\n{item.RuleType}\n{item.HitCount}\n{item.LastHitTime.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    }
}
