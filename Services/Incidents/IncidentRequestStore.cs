using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace LyWaf.Services.Incidents;

/// <summary>单次命中记录，保留可下钻的请求元数据，不包含认证信息或请求体原文。</summary>
public sealed record IncidentRequestRecord(string Id, string IncidentId, string Site, string Ip, string RuleId, IncidentSample Request);
public sealed record IncidentRequestPage(IncidentRequestRecord[] Data, long Total, string? NextCursor, long Dropped);

/// <summary>有界队列及滚动文件记录。请求线程不等待磁盘，查询流式扫描并仅保留一页记录。</summary>
public sealed class IncidentRequestStore : BackgroundService
{
    private const long FileLimit = 2 * 1024 * 1024;
    private readonly Channel<IncidentRequestRecord> queue = Channel.CreateBounded<IncidentRequestRecord>(512);
    private readonly SemaphoreSlim files = new(1, 1);
    private readonly string directory;
    private readonly ILogger<IncidentRequestStore> logger;
    private string? current;
    private long dropped;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IncidentRequestStore(ILogger<IncidentRequestStore> logger) : this(logger,
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LyWaf.Shared.SharedData.ConfigFilePath))!, ".lywaf.incident-requests")) { }
    internal IncidentRequestStore(ILogger<IncidentRequestStore> logger, string directory) { this.logger = logger; this.directory = directory; }

    public void Record(SecurityIncident incident)
    {
        if (incident.Samples.LastOrDefault() is not { } sample) return;
        sample = sample with
        {
            RequestId = Limit(sample.RequestId, 200), Method = Limit(sample.Method, 32), Path = Limit(sample.Path, 256),
            ContentType = Limit(sample.ContentType, 100), QueryKeys = sample.QueryKeys.Take(5).Select(x => Limit(x, 32)).ToArray(),
            MatchedFields = sample.MatchedFields.Take(30).Select(x => Limit(x, 100)).ToArray(),
            NormalizationSteps = sample.NormalizationSteps.Take(30).Select(x => Limit(x, 100)).ToArray(),
            MatchLocations = sample.MatchLocations?.Take(20).Select(x => x with
            { Field = Limit(x.Field, 100), FieldName = x.FieldName == null ? null : Limit(x.FieldName, 128), Operator = Limit(x.Operator, 100) }).ToArray()
        };
        var item = new IncidentRequestRecord(Guid.NewGuid().ToString("N"), incident.Id, Limit(incident.Site, 253), Limit(incident.Ip, 64), Limit(incident.RuleId, 200), sample);
        if (!queue.Writer.TryWrite(item)) Interlocked.Increment(ref dropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await files.WaitAsync(stoppingToken);
                try
                {
                    var line = JsonSerializer.Serialize(item, Json);
                    if (Encoding.UTF8.GetByteCount(line) > 32768) { Interlocked.Increment(ref dropped); continue; }
                    Directory.CreateDirectory(directory);
                    if (current == null || !File.Exists(current) || new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) + 1 > FileLimit)
                    {
                        current = Path.Combine(directory, $"requests-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.jsonl");
                        foreach (var old in Directory.EnumerateFiles(directory, "requests-*.jsonl").OrderByDescending(x => x, StringComparer.Ordinal).Skip(7))
                            File.Delete(old);
                    }
                    await File.AppendAllTextAsync(current, line + "\n", Encoding.UTF8, stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref dropped);
                    logger.LogWarning(ex, "请求事件记录写入失败");
                }
                finally { files.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        if (ExecuteTask != null) await ExecuteTask.WaitAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    public async Task<IncidentRequestPage> ReadAsync(string incidentId, string? cursor, int size, CancellationToken token)
    {
        size = Math.Clamp(size, 1, 100);
        (long Ticks, string Id)? before = null;
        if (cursor != null)
        {
            var pieces = cursor.Split(':');
            if (pieces.Length != 2 || cursor.Length > 64 || !long.TryParse(pieces[0], out var ticks) || !Guid.TryParseExact(pieces[1], "N", out _))
                throw new ArgumentException("请求记录游标无效", nameof(cursor));
            before = (ticks, pieces[1]);
        }
        var compare = Comparer<(long Ticks, string Id)>.Create((a, b) => a.Ticks != b.Ticks ? a.Ticks.CompareTo(b.Ticks) : StringComparer.Ordinal.Compare(a.Id, b.Id));
        var retained = new PriorityQueue<IncidentRequestRecord, (long Ticks, string Id)>(compare);
        long total = 0;
        long remaining = 0;
        await files.WaitAsync(token);
        try
        {
            if (!Directory.Exists(directory)) return new([], 0, null, Interlocked.Read(ref dropped));
            foreach (var path in Directory.EnumerateFiles(directory, "requests-*.jsonl").OrderByDescending(x => x, StringComparer.Ordinal).Take(8))
            {
                if (new FileInfo(path).Length > FileLimit) continue;
                using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true));
                string? line;
                while ((line = await input.ReadLineAsync(token)) != null)
                {
                    if (line.Length > 32768) continue;
                    IncidentRequestRecord? item;
                    try { item = JsonSerializer.Deserialize<IncidentRequestRecord>(line, Json); }
                    catch (JsonException) { continue; }
                    if (item?.IncidentId != incidentId || item.Request == null || item.Id == null) continue;
                    total++;
                    var key = (item.Request.Time.UtcTicks, item.Id);
                    if (before is { } limit && compare.Compare(key, limit) >= 0) continue;
                    remaining++;
                    retained.Enqueue(item, key);
                    if (retained.Count > size) retained.Dequeue();
                }
            }
        }
        finally { files.Release(); }
        var result = retained.UnorderedItems.OrderByDescending(x => x.Priority, compare).Select(x => x.Element).ToArray();
        var last = result.LastOrDefault();
        return new(result, total, remaining > size && last != null ? $"{last.Request.Time.UtcTicks}:{last.Id}" : null, Interlocked.Read(ref dropped));
    }

    private static string Limit(string value, int length) => value[..Math.Min(value.Length, length)];
}
