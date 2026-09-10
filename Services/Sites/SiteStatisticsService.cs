using System.Text.Json;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Services.Sites;

/// <summary>站点当日计数。持久化周期为 30 秒，异常终止最多损失一个周期的计数。</summary>
public sealed record SiteTraffic(DateOnly Day, DateTimeOffset ObservedSince, long Requests, long Intercepts, long Errors);

public sealed class SiteStatisticsService(ILogger<SiteStatisticsService> logger) : BackgroundService
{
    private readonly object gate = new();
    private Dictionary<string, SiteTraffic> counts = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly currentDay = DateOnly.FromDateTime(DateTime.Now);
    private readonly string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LyWaf.Shared.SharedData.ConfigFilePath))!, ".lywaf.site-statistics.json");
    private readonly SemaphoreSlim saving = new(1, 1);

    public void Record(string id, bool intercepted, int status)
    {
        lock (gate)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (currentDay != today) { counts.Clear(); currentDay = today; }
            if (!counts.TryGetValue(id, out var current))
            {
                if (counts.Count >= 5000) return;
                current = new(today, DateTimeOffset.UtcNow, 0, 0, 0);
            }
            counts[id] = current with { Requests = current.Requests + 1, Intercepts = current.Intercepts + (intercepted ? 1 : 0), Errors = current.Errors + (status >= 500 ? 1 : 0) };
        }
    }

    public SiteTraffic? Get(string id)
    {
        lock (gate) return counts.TryGetValue(id, out var value) && value.Day == DateOnly.FromDateTime(DateTime.Now) ? value : null;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024)
            {
                await using var stream = File.OpenRead(path);
                var saved = await JsonSerializer.DeserializeAsync<Dictionary<string, SiteTraffic>>(stream, cancellationToken: cancellationToken);
                if (saved != null) lock (gate) counts = new(saved.Where(p => p.Value.Day == DateOnly.FromDateTime(DateTime.Now)).Take(5000), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { logger.LogWarning(ex, "站点计数恢复失败"); }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await Save(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Save(cancellationToken);
    }

    private async Task Save(CancellationToken ct)
    {
        await saving.WaitAsync(ct);
        var temp = path + ".tmp";
        try
        {
            Dictionary<string, SiteTraffic> copy;
            lock (gate) copy = new(counts);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(copy), ct);
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "站点计数保存失败"); }
        finally { if (File.Exists(temp)) File.Delete(temp); saving.Release(); }
    }
}

public sealed class SiteStatisticsMiddleware(RequestDelegate next, SiteStatisticsService statistics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally { if (route != null) statistics.Record(route, context.Items.ContainsKey("LyWaf.Intercepted"), failed ? 500 : context.Response.StatusCode); }
    }
}
