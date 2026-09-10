using System.Diagnostics;
using LyWaf.Services.Acme;
using LyWaf.Services.DuckDb;
using LyWaf.Services.WafRule;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Yarp.ReverseProxy;

namespace LyWaf.Services.ManagementTasks;

/// <summary>健康面板的只读快照，不包含配置密钥或可执行操作。</summary>
public sealed record ManagementHealthSnapshot(DateTimeOffset CapturedAt, ManagementProcessHealth Process,
    string[] Listeners, ManagementUpstreamHealth[]? Upstreams, IReadOnlyList<CertificateStatusSnapshot>? Certificates,
    string Database, ManagementDiskHealth Disk, RulePackageState? Rules, ManagementQueueHealth Queue);
public sealed record ManagementProcessHealth(int Id, DateTime StartedAt, long MemoryBytes, int Threads);
public sealed record ManagementUpstreamHealth(string Cluster, string Destination, string Active, string Passive, int Connections, string Address, string[] Hosts);
public sealed record ManagementDiskHealth(long? AvailableBytes, long? CapacityBytes);
public sealed record ManagementQueueHealth(int Queued, int Running, int Failed);

/// <summary>运行态健康聚合查询。仅读取本机状态，不启动证书签发、数据库连接或远程探测。</summary>
public sealed class ManagementHealthService(IServiceProvider services, ManagementJobService jobs, IHostEnvironment environment)
{
    public ManagementHealthSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        var proxy = services.GetService<IProxyStateLookup>();
        var hosts = proxy?.GetRoutes().Select(r => r.Config).Where(r => r.ClusterId != null)
            .GroupBy(r => r.ClusterId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(r => r.Match.Hosts is { Count: > 0 } names ? names : new[] { "*" })
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var upstreams = proxy?.GetClusters().SelectMany(cluster => cluster.Destinations.Values.Select(destination => new ManagementUpstreamHealth(
            cluster.ClusterId, destination.DestinationId, destination.Health.Active.ToString(), destination.Health.Passive.ToString(),
            destination.ConcurrentRequestCount, destination.Model.Config.Address, hosts?.GetValueOrDefault(cluster.ClusterId) ?? []))).ToArray();
        var certificates = services.GetService<IAcmeService>()?.GetCertificateStatuses();
        var listeners = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
        long? available = null;
        long? capacity = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(environment.ContentRootPath))!);
            if (drive.IsReady) { available = drive.AvailableFreeSpace; capacity = drive.TotalSize; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var tasks = jobs.List();
        return new ManagementHealthSnapshot(DateTimeOffset.UtcNow,
            new(process.Id, process.StartTime.ToUniversalTime(), process.WorkingSet64, process.Threads.Count),
            listeners, upstreams, certificates, services.GetService<IDuckDbService>()?.HealthStatus ?? "unavailable",
            new(available, capacity), services.GetService<IRulePackageService>()?.GetState(),
            new(tasks.Count(t => t.State == ManagementJobState.Queued), tasks.Count(t => t.State == ManagementJobState.Running),
                tasks.Count(t => t.State is ManagementJobState.Failed or ManagementJobState.TimedOut)));
    }
}
