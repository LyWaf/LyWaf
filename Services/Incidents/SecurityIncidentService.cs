using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Protect;
using LyWaf.Services.WafRule;
using LyWaf.Utils;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Services.Incidents;

public enum IncidentDisposition { Pending, FalsePositive, RuleAdjusted, Ignored }
/// <summary>有限请求样本，不保留认证头、Cookie 或请求体原文。</summary>
public sealed record IncidentSample(string RequestId, DateTimeOffset Time, string Method, string Path, string ContentType,
    string[] QueryKeys, string[] MatchedFields, string[] NormalizationSteps, bool BodyTruncated, WafMatchLocation[]? MatchLocations = null);
public sealed record IncidentNote(DateTimeOffset Time, string Operator, IncidentDisposition State, string Note);
public sealed record SecurityIncident(string Id, string Site, string Host, string Ip, string RuleId, string AttackType,
    long Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, IncidentDisposition State, int StateVersion,
    IncidentSample[] Samples, IncidentNote[] History);

/// <summary>有界事件聚合及误报处置存储，处置立即落盘，流量计数按周期刷写。</summary>
public sealed class SecurityIncidentService : BackgroundService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim persistence = new(1, 1);
    private readonly Dictionary<string, SecurityIncident> events = new(StringComparer.Ordinal);
    private readonly string path;
    private readonly ILogger<SecurityIncidentService> logger;
    private long dropped;

    public SecurityIncidentService(ILogger<SecurityIncidentService> logger) : this(logger,
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LyWaf.Shared.SharedData.ConfigFilePath))!, ".lywaf.incidents.json")) { }
    internal SecurityIncidentService(ILogger<SecurityIncidentService> logger, string path) { this.logger = logger; this.path = path; }
    public long Dropped => Interlocked.Read(ref dropped);

    public SecurityIncident? Record(HttpContext context)
    {
        var rule = Limit(context.Items["LyWaf.RuleId"]?.ToString() ?? context.Items["LyWaf.InterceptReason"]?.ToString() ?? "access-control", 200);
        var type = Limit(context.Items["LyWaf.AttackType"]?.ToString() ?? "WAF", 100);
        var site = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId ?? context.Request.Host.Value ?? "unknown";
        var host = (context.Request.Host.Value ?? "").ToLowerInvariant();
        var ip = RequestUtil.GetClientIp(context.Request) ?? "unknown";
        var id = Fingerprint(site, host, ip, rule, type);
        var now = DateTimeOffset.UtcNow;
        var inspection = RequestInspectionService.GetCompletedSnapshot(context);
        var sample = new IncidentSample(context.TraceIdentifier, now, context.Request.Method, Limit(context.Request.Path.Value ?? "/", 256),
            Limit(context.Request.ContentType ?? "", 100), context.Request.Query.Keys.Take(5).Select(k => Limit(k, 32)).ToArray(),
            context.Items["LyWaf.MatchedFields"] as string[] ?? [], inspection?.NormalizationSteps?.Take(30).ToArray() ?? [], inspection?.BodyTruncated ?? false,
            context.Items["LyWaf.MatchLocations"] as WafMatchLocation[] ?? []);
        lock (gate)
        {
            if (!events.TryGetValue(id, out var item))
            {
                if (events.Count >= 1000) { Interlocked.Increment(ref dropped); return null; }
                item = new(id, site, host, ip, rule, type, 0, now, now, IncidentDisposition.Pending, 0, [], []);
            }
            events[id] = item with { Count = item.Count + 1, LastSeen = now, Samples = item.Samples.Append(sample).TakeLast(3).ToArray() };
            return events[id];
        }
    }

    public SecurityIncident[] List() { lock (gate) return events.Values.OrderByDescending(e => e.LastSeen).Select(e => e with { Samples = [], History = [] }).ToArray(); }
    public SecurityIncident? Find(string id) { lock (gate) return events.TryGetValue(id, out var item) ? Clone(item) : null; }

    public async Task<bool> SetDisposition(string id, IncidentDisposition state, string note, string actor, int expectedVersion, CancellationToken ct)
    {
        if (!Enum.IsDefined(state) || string.IsNullOrWhiteSpace(note) || note.Length > 200) throw new ArgumentException("处置备注必填且不能超过 200 字符");
        await persistence.WaitAsync(ct);
        SecurityIncident? before = null;
        try
        {
            lock (gate)
            {
                if (!events.TryGetValue(id, out before) || before.StateVersion != expectedVersion) return false;
                events[id] = before with { State = state, StateVersion = before.StateVersion + 1,
                    History = before.History.Append(new IncidentNote(DateTimeOffset.UtcNow, actor, state, note)).TakeLast(10).ToArray() };
            }
            await Save(ct);
            return true;
        }
        catch
        {
            if (before != null) lock (gate)
            {
                var current = events[id];
                events[id] = current with { State = before.State, StateVersion = before.StateVersion, History = before.History };
            }
            throw;
        }
        finally { persistence.Release(); }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("事件存储超过容量限制");
            await using var stream = File.OpenRead(path);
            var saved = await JsonSerializer.DeserializeAsync<SecurityIncident[]>(stream, cancellationToken: cancellationToken) ?? [];
            lock (gate) foreach (var item in saved.Take(1000)) events[item.Id] = item with { Samples = item.Samples.TakeLast(3).ToArray(), History = item.History.TakeLast(10).ToArray() };
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await Flush(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Flush(cancellationToken);
    }

    private async Task Flush(CancellationToken ct)
    {
        await persistence.WaitAsync(ct);
        try { await Save(ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "事件存储写入失败"); }
        finally { persistence.Release(); }
    }

    private async Task Save(CancellationToken ct)
    {
        SecurityIncident[] snapshot;
        lock (gate) snapshot = events.Values.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static string Fingerprint(params string[] values) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    private static string Limit(string value, int length) => value[..Math.Min(value.Length, length)];
    private static SecurityIncident Clone(SecurityIncident item) => item with { Samples = item.Samples.Select(s => s with
        { QueryKeys = [.. s.QueryKeys], MatchedFields = [.. s.MatchedFields], NormalizationSteps = [.. s.NormalizationSteps], MatchLocations = s.MatchLocations?.ToArray() }).ToArray(), History = [.. item.History] };
}

public sealed class SecurityIncidentMiddleware(RequestDelegate next, SecurityIncidentService incidents,
    LyWaf.Services.Notifications.NotificationService notifications, IncidentRequestStore requests, ILogger<SecurityIncidentMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        finally
        {
            if (context.Items.ContainsKey("LyWaf.Intercepted"))
            {
                try
                {
                    if (incidents.Record(context) is { } incident)
                    {
                        requests.Record(incident);
                        notifications.Observe(incident);
                    }
                }
                catch (Exception ex) { logger.LogWarning(ex, "记录安全事件失败"); }
            }
        }
    }
}
