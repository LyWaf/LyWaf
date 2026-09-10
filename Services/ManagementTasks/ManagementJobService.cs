using System.Threading.Channels;

namespace LyWaf.Services.ManagementTasks;

/// <summary>管理任务的执行状态；取消和超时在执行器退出后确认，避免后台仍在修改配置。</summary>
public enum ManagementJobState { Queued, Running, Succeeded, Failed, Cancelled, TimedOut }

/// <summary>管理请求受理后返回任务快照，受理不等同于执行成功。</summary>
public sealed record ManagementJobResponse(bool Success, ManagementJobSnapshot Data);

/// <summary>任务快照不包含执行委托、请求凭据或可变日志集合。</summary>
public sealed record ManagementJobSnapshot(string Id, string Kind, string Operator, ManagementJobState State,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string[] Logs,
    string? Error, bool CancellationRequested, bool CanRetry, string? RetryOf, string[] ResourceIds);

/// <summary>有界串行管理任务队列。执行器必须遵守取消令牌，不允许脱离队列继续修改系统。</summary>
public sealed class ManagementJobService : BackgroundService
{
    private sealed class Entry(string kind, string actor, Func<Action<string>, CancellationToken, Task> execute, TimeSpan timeout, string? retryOf)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly string Kind = kind;
        public readonly string Actor = actor;
        public readonly Func<Action<string>, CancellationToken, Task> Execute = execute;
        public readonly TimeSpan Timeout = timeout;
        public readonly string? RetryOf = retryOf;
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Queue<string> Logs = new();
        public ManagementJobState State = ManagementJobState.Queued;
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? FinishedAt;
        public string? Error;
        public bool CancelRequested;
        public string[] ResourceIds = [];
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private bool stopping;

    public ManagementJobSnapshot Enqueue(string kind, string actor, Func<Action<string>, CancellationToken, Task> execute,
        TimeSpan? timeout = null, string[]? resourceIds = null) => EnqueueCore(kind, actor, execute, timeout ?? TimeSpan.FromMinutes(5), null, resourceIds ?? []);

    private ManagementJobSnapshot EnqueueCore(string kind, string actor, Func<Action<string>, CancellationToken, Task> execute,
        TimeSpan timeout, string? retryOf, string[] resourceIds)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || actor.Length > 200 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
            throw new ArgumentException("任务参数无效");
        lock (gate)
        {
            if (stopping) throw new InvalidOperationException("任务服务正在停止");
            var entry = new Entry(kind, actor, execute, timeout, retryOf);
            entry.ResourceIds = [.. resourceIds];
            entries.Add(entry.Id, entry);
            if (!queue.Writer.TryWrite(entry))
            {
                entries.Remove(entry.Id);
                entry.Cancellation.Dispose();
                throw new InvalidOperationException("后台任务队列已满");
            }
            foreach (var old in entries.Values.Where(e => e.FinishedAt != null).OrderByDescending(e => e.FinishedAt).Skip(200).ToArray())
            { entries.Remove(old.Id); old.Cancellation.Dispose(); }
            return Snapshot(entry);
        }
    }

    public ManagementJobSnapshot[] List()
    {
        lock (gate) return entries.Values.OrderByDescending(e => e.CreatedAt).Select(Snapshot).ToArray();
    }

    public ManagementJobSnapshot? Find(string id)
    {
        lock (gate) return entries.TryGetValue(id, out var entry) ? Snapshot(entry) : null;
    }

    public bool Cancel(string id)
    {
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(id, out entry!) || entry.State is not (ManagementJobState.Queued or ManagementJobState.Running)) return false;
            entry.CancelRequested = true;
            if (entry.State == ManagementJobState.Queued)
            { entry.State = ManagementJobState.Cancelled; entry.FinishedAt = DateTimeOffset.UtcNow; }
            // CancelAsync 不在锁内调用执行器注册的取消回调。
            _ = entry.Cancellation.CancelAsync();
        }
        return true;
    }

    public ManagementJobSnapshot? Retry(string id, string actor)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var entry) || entry.State is not (ManagementJobState.Failed or ManagementJobState.Cancelled or ManagementJobState.TimedOut)) return null;
            return EnqueueCore(entry.Kind, actor, entry.Execute, entry.Timeout, entry.Id, entry.ResourceIds);
        }
    }

    /// <summary>接管提交后关闭队列，尚未执行的操作不得在旧进程中继续发布配置。</summary>
    public void StopAcceptingForShutdown()
    {
        lock (gate)
        {
            stopping = true;
            queue.Writer.TryComplete();
            foreach (var entry in entries.Values.Where(entry => entry.State == ManagementJobState.Queued))
            {
                entry.State = ManagementJobState.Cancelled;
                entry.FinishedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stoppingToken))
            {
                lock (gate)
                {
                    if (entry.State != ManagementJobState.Queued) continue;
                    entry.State = ManagementJobState.Running;
                    entry.StartedAt = DateTimeOffset.UtcNow;
                }
                using var timeout = new CancellationTokenSource(entry.Timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, entry.Cancellation.Token, timeout.Token);
                try
                {
                    await entry.Execute(message => AppendLog(entry, message), linked.Token);
                    // Normal return is the executor's success acknowledgement. A late cancellation
                    // cannot undo its committed side effects or make them safe to execute again.
                    if (linked.IsCancellationRequested) AppendLog(entry, "任务已完成；取消或超时请求未撤销已提交的结果");
                    lock (gate) entry.State = ManagementJobState.Succeeded;
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    lock (gate) entry.State = timeout.IsCancellationRequested && !entry.CancelRequested && !stoppingToken.IsCancellationRequested
                        ? ManagementJobState.TimedOut : ManagementJobState.Cancelled;
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        entry.State = ex is TimeoutException ? ManagementJobState.TimedOut : ManagementJobState.Failed;
                        entry.Error = ex is ArgumentException or InvalidDataException or TimeoutException
                            ? ex.Message[..Math.Min(ex.Message.Length, 1000)] : ex.GetType().Name;
                    }
                }
                finally { lock (gate) entry.FinishedAt = DateTimeOffset.UtcNow; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                stopping = true;
                queue.Writer.TryComplete();
                foreach (var entry in entries.Values.Where(e => e.State == ManagementJobState.Queued))
                { entry.State = ManagementJobState.Cancelled; entry.FinishedAt = DateTimeOffset.UtcNow; }
            }
        }
    }

    private void AppendLog(Entry entry, string message)
    {
        lock (gate)
        {
            if (entry.FinishedAt != null) return;
            if (entry.Logs.Count == 100) entry.Logs.Dequeue();
            entry.Logs.Enqueue($"{DateTimeOffset.UtcNow:O} {message[..Math.Min(message.Length, 2000)]}");
        }
    }

    private static ManagementJobSnapshot Snapshot(Entry e) => new(e.Id, e.Kind, e.Actor, e.State, e.CreatedAt, e.StartedAt,
        e.FinishedAt, e.Logs.ToArray(), e.Error, e.CancelRequested,
        e.State is ManagementJobState.Failed or ManagementJobState.Cancelled or ManagementJobState.TimedOut, e.RetryOf, [.. e.ResourceIds]);
}
