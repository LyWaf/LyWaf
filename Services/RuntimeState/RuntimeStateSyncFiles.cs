using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

internal sealed class RuntimeStateSyncFiles(string stateDirectory, RuntimeStateOptions? capacityOptions = null)
{
    private const long MaximumDirectoryBytes = 64L * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetFullPath(stateDirectory), "runtime-sync");
    private sealed record Document<T>(T Value, string Digest);
    internal Action<IOException>? ReadOpenConflict { get; set; }
    internal Action? BeforeReadOpen { get; set; }

    internal string PathFor(string direction, string cluster, string sender, string receiver)
    {
        if (!RuntimeStateSyncProtocol.ValidIdentity(cluster) || !RuntimeStateSyncProtocol.ValidIdentity(sender)
            || !RuntimeStateSyncProtocol.ValidIdentity(receiver)) throw new InvalidDataException("Invalid sync identity");
        Directory.CreateDirectory(root);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { cluster, sender, receiver })));
        return Path.Combine(root, direction + "-" + digest + ".json");
    }

    internal FileStream Lock(string path) => AcquireExclusiveLock(path + ".lock", CancellationToken.None);

    internal FileStream? LockWhileMissing(string path)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!File.Exists(path))
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            {
                // A writer may publish the first checkpoint without yielding its lock to a polling reader.
                if (File.Exists(path)) return null;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) throw;
                ReadOpenConflict?.Invoke(error);
                Thread.Sleep(10);
            }
        }
        return null;
    }

    internal T? Read<T>(string path, bool requireExisting = false) where T : class
    {
        if (!requireExisting && !File.Exists(path)) return null;
        BeforeReadOpen?.Invoke();
        using var stream = OpenRead(path);
        if (stream.Length > RuntimeStateSyncProtocol.MaximumBatchBytes + 1024 * 1024)
            throw new InvalidDataException("Oversized runtime sync checkpoint");
        using var raw = JsonDocument.Parse(stream);
        if (raw.RootElement.ValueKind != JsonValueKind.Object || !raw.RootElement.TryGetProperty("value", out var value)
            || !raw.RootElement.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid runtime sync checkpoint envelope");
        if (digest.GetString() != Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value.GetRawText()))))
            throw new InvalidDataException("Corrupt runtime sync checkpoint");
        return value.Deserialize<T>(RuntimeStateSyncProtocol.Json) ?? throw new InvalidDataException("Missing runtime sync checkpoint");
    }

    private FileStream OpenRead(string path)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 2 or 32 or 33
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                // Delete sharing permits replacement but cannot prevent a transient native open conflict.
                // Retry only opening: never mask corrupt content or report an unreadable cursor as absent.
                ReadOpenConflict?.Invoke(error);
                Thread.Sleep(10);
            }
        }
    }

    internal void Save<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        using var quota = AcquireQuotaLock(cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document<T>(value, Digest(value)), RuntimeStateSyncProtocol.Json);
        using var directoryBudget = RuntimeStateDiskBudget.Reserve(capacityOptions ?? new RuntimeStateOptions { StateDirectory = stateDirectory },
            bytes.LongLength, File.Exists(path + ".tmp") ? 0 : 1, cancellationToken);
        var files = new DirectoryInfo(root).GetFiles();
        if (bytes.Length > RuntimeStateSyncProtocol.MaximumBatchBytes + 1024 * 1024
            || files.Count(f => f.Extension == ".json") >= 10000 && !File.Exists(path)
            || files.Sum(f => f.Length) + bytes.Length > MaximumDirectoryBytes)
            throw new IOException("Runtime sync durable quota exhausted");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ReplaceDurablyStaged(temporary, path, cancellationToken);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ReplaceDurablyStaged(string temporary, string path, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
                else File.Move(temporary, path);
                return;
            }
            catch (Exception ex) when (IsTransientReplacementConflict(ex, path)
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                if (token.WaitHandle.WaitOne(10)) token.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool IsTransientReplacementConflict(Exception error, string path)
    {
        if (error is IOException && (error.HResult & 0xffff) is 11 or 32 or 33) return true;
        // Windows replacement can report ACCESS_DENIED while a reader closes a delete-shared handle.
        // Never clear read-only attributes or fall back to delete/copy; a persistent denial still fails.
        return OperatingSystem.IsWindows() && error is UnauthorizedAccessException
            && (error.HResult & 0xffff) == 5 && File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
    }

    private FileStream AcquireQuotaLock(CancellationToken token) =>
        AcquireExclusiveLock(Path.Combine(root, ".quota.lock"), token);

    private static FileStream AcquireExclusiveLock(string path, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                // Only bounded local disk admission waits here, never network or peer ACK waits.
                if (token.WaitHandle.WaitOne(10)) token.ThrowIfCancellationRequested();
            }
        }
    }

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, RuntimeStateSyncProtocol.Json)));
}
