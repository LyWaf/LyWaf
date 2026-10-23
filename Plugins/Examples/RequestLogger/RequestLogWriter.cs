using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace LyWaf.Plugins.Examples.RequestLogger;

internal sealed record RequestLogWritePolicy(long MaxFileBytes, int MaxFiles, int RetentionDays, int BatchSize, int FlushIntervalMs);
internal sealed record RequestLogWriterStatus(long Enqueued, long Written, long Dropped, long WriteFailures, long Pending,
    long QueuedBytes = 0, long RotationFailures = 0, long CleanupFailures = 0,
    DateTimeOffset? LastErrorAt = null, string? LastErrorType = null, bool Healthy = true);

internal sealed class RequestLogWriter
{
    private sealed record Item(string? Path, byte[]? Bytes, RequestLogWritePolicy? Policy, TaskCompletionSource? Barrier = null);
    private sealed record FailureInfo(DateTimeOffset At, string Type);
    private sealed record Sweep(long At, int MaxFiles, int Days);
    private readonly Channel<Item> queue;
    private readonly SemaphoreSlim fileLock;
    private readonly Func<string, bool> ownsFile;
    private readonly Action<Exception> failure;
    private readonly Action<string> deleteFile;
    private readonly Action<string, string> moveFile;
    private readonly long byteCapacity;
    private readonly Task worker;
    private readonly Dictionary<string, Sweep> sweeps = new(StringComparer.Ordinal);
    private FailureInfo? lastError;
    private long queuedBytes, enqueued, written, dropped, failures, pending, rotationFailures, cleanupFailures;

    internal RequestLogWriter(int capacity, long byteCapacity, SemaphoreSlim fileLock, Func<string, bool> ownsFile,
        Action<Exception> failure, Action<string>? deleteFile = null, Action<string, string>? moveFile = null)
    {
        queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        this.byteCapacity = byteCapacity; this.fileLock = fileLock; this.ownsFile = ownsFile; this.failure = failure;
        this.deleteFile = deleteFile ?? File.Delete; this.moveFile = moveFile ?? File.Move;
        worker = RunAsync();
    }

    internal RequestLogWriterStatus Status
    {
        get
        {
            var error = Volatile.Read(ref lastError);
            return new(Interlocked.Read(ref enqueued), Interlocked.Read(ref written), Interlocked.Read(ref dropped),
                Interlocked.Read(ref failures), Interlocked.Read(ref pending), Interlocked.Read(ref queuedBytes),
                Interlocked.Read(ref rotationFailures), Interlocked.Read(ref cleanupFailures), error?.At, error?.Type, !worker.IsFaulted);
        }
    }

    internal bool TryWrite(string path, string entry, RequestLogWritePolicy policy)
    {
        var bytes = Encoding.UTF8.GetBytes(entry);
        if (Interlocked.Add(ref queuedBytes, bytes.Length) > byteCapacity)
        { Interlocked.Add(ref queuedBytes, -bytes.Length); Interlocked.Increment(ref dropped); return false; }
        Interlocked.Increment(ref pending);
        if (!queue.Writer.TryWrite(new(path, bytes, policy)))
        {
            Interlocked.Decrement(ref pending); Interlocked.Add(ref queuedBytes, -bytes.Length);
            Interlocked.Increment(ref dropped); return false;
        }
        Interlocked.Increment(ref enqueued);
        return true;
    }

    internal async Task FlushAsync(CancellationToken token = default)
    {
        if (worker.IsCompleted) { await worker; return; }
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await queue.Writer.WriteAsync(new(null, null, null, barrier), token); }
        catch (ChannelClosedException) { await worker.WaitAsync(token); return; }
        await barrier.Task.WaitAsync(token);
    }

    internal async Task StopAsync(CancellationToken token)
    {
        queue.Writer.TryComplete();
        await worker.WaitAsync(token);
    }

    private async Task RunAsync()
    {
        Task<bool>? ready = null;
        while (await (ready ?? queue.Reader.WaitToReadAsync().AsTask()))
        {
            ready = null;
            if (!queue.Reader.TryRead(out var first)) continue;
            var batch = new List<Item> { first };
            PeriodicTimer? timer = null;
            Task<bool>? deadline = null;
            try
            {
                var size = Math.Max(1, first.Policy?.BatchSize ?? 1);
                // Full batches and explicit flush barriers do not wait for a timer.
                while (batch.Count < size && batch[^1].Barrier is null)
                {
                    while (batch.Count < size && batch[^1].Barrier is null && queue.Reader.TryRead(out var item)) batch.Add(item);
                    if (batch.Count >= size || batch[^1].Barrier is not null) break;
                    timer ??= new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, first.Policy!.FlushIntervalMs)));
                    deadline ??= timer.WaitForNextTickAsync().AsTask();
                    ready ??= queue.Reader.WaitToReadAsync().AsTask();
                    if (await Task.WhenAny(ready, deadline) == deadline) break;
                    if (!await ready) break;
                    ready = null;
                }
            }
            finally { timer?.Dispose(); }

            await fileLock.WaitAsync();
            try
            {
                foreach (var group in batch.Where(item => item.Path is not null).GroupBy(item => (item.Path!, item.Policy!)))
                    await WriteBatchAsync(group.Key.Item1, group.Select(item => item.Bytes!).ToArray(), group.Key.Item2);
            }
            finally
            {
                fileLock.Release();
                foreach (var item in batch)
                {
                    if (item.Bytes is not null)
                    { Interlocked.Add(ref queuedBytes, -item.Bytes.Length); Interlocked.Decrement(ref pending); }
                    item.Barrier?.TrySetResult();
                }
            }
        }
    }

    private async Task WriteBatchAsync(string path, byte[][] entries, RequestLogWritePolicy policy)
    {
        FileStream? file = null;
        var confirmed = 0;
        var segment = 0;
        var rotated = false;
        try
        {
            EnsureSafePath(path);
            var directory = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            EnsureSafePath(path);
            foreach (var bytes in entries)
            {
                file ??= Open(path);
                if (file.Length > 0 && file.Length + bytes.Length > policy.MaxFileBytes)
                {
                    await ConfirmAsync();
                    await file.DisposeAsync(); file = null;
                    var archive = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "."
                        + DateTime.UtcNow.Ticks + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(path));
                    try { moveFile(path, archive); rotated = true; }
                    catch (Exception error) when (IsFileError(error))
                    { Interlocked.Increment(ref rotationFailures); throw; }
                    file = Open(path);
                }
                await file.WriteAsync(bytes);
                segment++;
            }
            await ConfirmAsync();
        }
        catch (Exception error) when (IsFileError(error))
        { Interlocked.Add(ref failures, entries.Length - confirmed); Report(error); }
        finally
        {
            if (file is not null)
                try { await file.DisposeAsync(); }
                catch (Exception error) when (IsFileError(error)) { Report(error); }
        }

        if (confirmed > 0) Maintain(path, policy, rotated);

        async Task ConfirmAsync()
        {
            await file!.FlushAsync();
            confirmed += segment;
            Interlocked.Add(ref written, segment);
            segment = 0;
        }
    }

    private void Maintain(string path, RequestLogWritePolicy policy, bool rotated)
    {
        var directory = Path.GetDirectoryName(path)!;
        var now = Stopwatch.GetTimestamp();
        if (!rotated && sweeps.TryGetValue(directory, out var last) && last.MaxFiles == policy.MaxFiles
            && last.Days == policy.RetentionDays && Stopwatch.GetElapsedTime(last.At, now) < TimeSpan.FromMinutes(1)) return;
        if (sweeps.Count >= 64 && !sweeps.ContainsKey(directory)) sweeps.Remove(sweeps.MinBy(pair => pair.Value.At).Key);
        sweeps[directory] = new(now, policy.MaxFiles, policy.RetentionDays);
        try
        {
            var files = new DirectoryInfo(directory).EnumerateFiles().Where(info => ownsFile(info.Name)
                && (info.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(info => info.FullName == path)
                .ThenByDescending(info => info.LastWriteTimeUtc).ThenByDescending(info => info.Name, StringComparer.Ordinal).ToArray();
            var cutoff = DateTime.UtcNow.AddDays(-policy.RetentionDays);
            for (var i = 0; i < files.Length; i++)
                if (files[i].FullName != path && (i >= policy.MaxFiles || files[i].LastWriteTimeUtc < cutoff))
                    try { deleteFile(files[i].FullName); }
                    catch (Exception error) when (IsFileError(error)) { Interlocked.Increment(ref cleanupFailures); Report(error); }
        }
        catch (Exception error) when (IsFileError(error)) { Interlocked.Increment(ref cleanupFailures); Report(error); }
    }

    private void Report(Exception error)
    {
        Volatile.Write(ref lastError, new(DateTimeOffset.UtcNow, error.GetType().Name));
        try { failure(error); } catch { /* A logging sink must not terminate this writer. */ }
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static FileStream Open(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }

    internal static void EnsureSafePath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Request log paths cannot contain links");
    }
}
