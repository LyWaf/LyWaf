using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using LyWaf.Services.ManagementTasks;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;

namespace LyWaf.Services.Sites;

/// <summary>站点以路由为稳定身份，完整保留 YARP 匹配、变换及集群引用。</summary>
public sealed record ManagedSite([property: Required, MaxLength(100)] string Id,
    [property: Required, MaxLength(200)] string Name, [property: Required] RouteConfig Route,
    bool Enabled = true, bool Deleted = false, [property: MaxLength(100)] string[]? RuleIds = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Route != null && Route.RouteId != Id)
            yield return new("路由 ID 必须与站点 ID 一致", [nameof(Route) + ".RouteId"]);
        if (RuleIds != null)
            for (var i = 0; i < RuleIds.Length; i++)
                if (string.IsNullOrWhiteSpace(RuleIds[i]) || RuleIds[i].Length > 200 || RuleIds[i].Any(char.IsControl))
                    yield return new("规则 ID 无效", [$"{nameof(RuleIds)}[{i}]"]);
    }
}
public sealed record SiteRevision(string Version, DateTimeOffset CreatedAt, string Operator, ManagedSite[] Sites);
public sealed record SiteDocument(int Schema, SiteRevision Current, SiteRevision[] History);

/// <summary>在原有配置提供器之上应用站点覆盖，保留配置发现，并支持删除与暂停原始路由。</summary>
public sealed class ManagedSiteProvider : IProxyConfigProvider, IDisposable
{
    public const string VersionKey = "LyWaf.SiteVersion";
    public const string RuleIdsKey = "LyWaf.SiteRuleIds";
    private sealed record Snapshot(IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters, IChangeToken ChangeToken) : IProxyConfig;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IProxyConfigProvider source;
    private readonly IServiceProvider services;
    private readonly string path;
    private readonly object sync = new();
    private readonly SemaphoreSlim publishing = new(1, 1);
    private readonly IDisposable subscription;
    private CancellationTokenSource changed = new();
    private SiteDocument document;
    private IProxyConfig snapshot;
    private int disposed;

    public ManagedSiteProvider(IProxyConfigProvider source, IServiceProvider services, string path)
    {
        this.source = source;
        this.services = services;
        this.path = Path.GetFullPath(path);
        if (File.Exists(path) && new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("站点配置超过 8 MiB 限制");
        document = File.Exists(path) ? JsonSerializer.Deserialize<SiteDocument>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("站点配置为空")
            : new(1, new("initial", DateTimeOffset.UtcNow, "system", []), []);
        if (document.Schema != 1 || document.Current?.Sites is not { Length: <= 1000 } || document.History is not { Length: <= 10 })
            throw new InvalidDataException("不支持的站点配置版本或条目数量无效");
        snapshot = Build();
        subscription = ChangeToken.OnChange(() => source.GetConfig().ChangeToken, Rebuild);
    }

    public IProxyConfig GetConfig() { lock (sync) return snapshot; }
    public SiteDocument Export() { lock (sync) return Clone(document); }
    public ManagedSite[] List()
    {
        lock (sync) return Effective(document.Current.Sites).Where(s => !s.Deleted).Select(Clone).ToArray();
    }

    public string Version { get { lock (sync) return document.Current.Version; } }

    public async Task PublishAsync(ManagedSite[] changes, string actor, Action<string> log, CancellationToken cancellationToken, string? expectedVersion = null)
    {
        await publishing.WaitAsync(cancellationToken);
        try
        {
            SiteDocument before;
            lock (sync) before = document;
            if (!string.IsNullOrEmpty(expectedVersion) && before.Current.Version != expectedVersion)
                throw new ArgumentException("站点配置已被修改，请刷新后重新提交");
            var sites = before.Current.Sites.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var site in changes)
            {
                if (string.IsNullOrWhiteSpace(site.Id) || site.Id.Length > 100 || site.Name?.Length > 200 || site.Route is null || site.Route.RouteId != site.Id)
                    throw new ArgumentException("站点 ID、名称或路由无效");
                sites[site.Id] = Clone(site);
            }
            if (sites.Count > 1000) throw new ArgumentException("站点覆盖不能超过 1000 项");
            var revision = new SiteRevision(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, actor, sites.Values.ToArray());
            var candidate = new SiteDocument(1, revision, new[] { before.Current }.Concat(before.History).Take(10).ToArray());
            log("校验站点及路由配置");
            var validator = services.GetRequiredService<IConfigValidator>();
            var clusters = source.GetConfig().Clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var site in changes.Where(s => !s.Deleted))
            {
                if (site.Route.ClusterId is null || !clusters.Contains(site.Route.ClusterId)) throw new ArgumentException("站点必须引用已存在的上游集群");
                var errors = await validator.ValidateRouteAsync(site.Route);
                if (errors.Count != 0) throw new ArgumentException("站点路由配置校验失败: " + errors[0].Message);
                if (site.RuleIds is { } ids)
                {
                    var rules = services.GetRequiredService<LyWaf.Services.WafRule.IWafRuleService>();
                    if (ids.Length > 100 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Contains('\n') || rules.GetRule(id) is null))
                        throw new ArgumentException("站点绑定的 WAF 规则不存在或数量过多");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            log($"持久化站点版本 {revision.Version}");
            await Save(candidate, cancellationToken);
            try
            {
                lock (sync) document = candidate;
                Rebuild();
                log("发布站点配置，等待运行态确认");
                await Confirm(changes, revision.Version, cancellationToken);
                log($"站点版本已生效 {revision.Version}");
            }
            catch
            {
                log("发布未确认，恢复最近有效站点配置");
                await Save(before, CancellationToken.None);
                lock (sync) document = before;
                Rebuild();
                var previous = Effective(before.Current.Sites).ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
                await Confirm(changes.Select(s => previous.GetValueOrDefault(s.Id) ?? s with { Deleted = true }).ToArray(), before.Current.Version, CancellationToken.None);
                log("站点配置已恢复");
                throw;
            }
        }
        finally { publishing.Release(); }
    }

    private async Task Confirm(ManagedSite[] changes, string version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var runtime = services.GetRequiredService<IProxyStateLookup>();
        try
        {
            while (changes.Any(site =>
            {
                var exists = runtime.TryGetRoute(site.Id, out var route);
                return site.Deleted || !site.Enabled ? exists : !exists || route!.Config.Metadata?.GetValueOrDefault(VersionKey) != version;
            })) await Task.Delay(50, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("站点运行态生效确认超时"); }
    }

    private ManagedSite[] Effective(ManagedSite[] overrides)
    {
        var sites = source.GetConfig().Routes.ToDictionary(r => r.RouteId,
            r => new ManagedSite(r.RouteId, r.RouteId, r), StringComparer.OrdinalIgnoreCase);
        foreach (var site in overrides) sites[site.Id] = site;
        return sites.Values.ToArray();
    }

    private IProxyConfig Build()
    {
        var routes = Effective(document.Current.Sites).Where(s => s.Enabled && !s.Deleted).Select(s =>
        {
            var metadata = new Dictionary<string, string>(s.Route.Metadata ?? new Dictionary<string, string>()) { [VersionKey] = document.Current.Version };
            if (s.RuleIds != null) metadata[RuleIdsKey] = string.Join('\n', s.RuleIds);
            else metadata.Remove(RuleIdsKey);
            return s.Route with { Metadata = metadata };
        }).ToArray();
        return new Snapshot(routes, source.GetConfig().Clusters, new CancellationChangeToken(changed.Token));
    }

    private void Rebuild()
    {
        CancellationTokenSource previous;
        lock (sync)
        {
            previous = changed;
            changed = new CancellationTokenSource();
            snapshot = Build();
        }
        try { previous.Cancel(); } finally { previous.Dispose(); }
    }

    private async Task Save(SiteDocument value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (json.Length > 8 * 1024 * 1024) throw new ArgumentException("站点配置及版本历史超过容量限制");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { await File.WriteAllTextAsync(temp, json, ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        subscription.Dispose();
        changed.Dispose();
    }
}

public static class ManagedSiteRegistration
{
    /// <summary>包装已注册的唯一配置源，不创建第二套可能与原路由冲突的 YARP 路由。</summary>
    public static void AddManagedSites(this IServiceCollection services, string? storePath = null)
    {
        var original = services.Last(d => d.ServiceType == typeof(IProxyConfigProvider));
        services.Remove(original);
        services.AddSingleton(sp =>
        {
            var inner = original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return new ManagedSiteProvider((IProxyConfigProvider)inner, sp,
                storePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LyWaf.Shared.SharedData.ConfigFilePath))!, ".lywaf.sites.json"));
        });
        services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<ManagedSiteProvider>());
    }
}
