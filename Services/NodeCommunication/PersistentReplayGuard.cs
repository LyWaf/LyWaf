using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace LyWaf.Services.NodeCommunication;

internal enum ReplayClaim { Accepted, Replay, CapacityExceeded, Unavailable }

/// <summary>Durably claims authenticated nonces before any command can execute.</summary>
internal static class PersistentReplayGuard
{
    private static readonly ActivitySource Diagnostics = new("LyWaf.Node.Replay");
    internal static ReplayClaim Claim(string directory, string identity, long now, int capacity = 10000)
        => ClaimAsync(directory, identity, now, capacity: capacity).GetAwaiter().GetResult();

    internal static async Task<ReplayClaim> ClaimAsync(string directory, string identity, long now,
        CancellationToken cancellationToken = default, int capacity = 10000, TimeSpan? lockTimeout = null, Action? beforeFlush = null)
    {
        var timeout = lockTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        var elapsed = Stopwatch.StartNew();
        var phase = "directory";
        FileStream? pending = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return ReplayClaim.Unavailable;
            var lockPath = Path.Combine(directory, ".lock");
            phase = "lock-attributes";
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                return ReplayClaim.Unavailable;
            FileStream gate;
            phase = "lock-acquire";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException ex) when (IsLockContention(ex))
                {
                    var remaining = timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        NLog.LogManager.GetCurrentClassLogger().Warn("Replay claim unavailable: lock-timeout after {Milliseconds}ms", elapsed.ElapsedMilliseconds);
                        return ReplayClaim.Unavailable;
                    }
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            ReplayClaim claim;
            using (gate)
            {
                phase = "nonce-storage";
                claim = ClaimUnderLock(directory, identity, now, capacity, cancellationToken, out pending);
            }
            if (claim != ReplayClaim.Accepted) return claim;
            phase = "nonce-fsync";
            using var flushActivity = Diagnostics.StartActivity("replay.claim.fsync");
            beforeFlush?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            // The complete reservation is visible under the directory lock, but this caller
            // cannot execute until its own file is durable. Unrelated nonces may flush concurrently.
            pending!.Flush(flushToDisk: true);
            return ReplayClaim.Accepted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn("Replay claim unavailable: phase={Phase} error={Error} code={Code}",
                phase, ex.GetType().Name, ex.HResult & 0xffff);
            return ReplayClaim.Unavailable;
        }
        finally { pending?.Dispose(); }
    }

    private static bool IsLockContention(IOException exception)
        => (exception.HResult & 0xffff) is 32 or 33 ||
            (!OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 11);

    private static ReplayClaim ClaimUnderLock(string directory, string identity, long now, int capacity,
        CancellationToken cancellationToken, out FileStream? pending)
    {
            pending = null;
            using var activity = Diagnostics.StartActivity("replay.claim.storage");
            var timer = Stopwatch.StartNew();
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".nonce";
            var count = 0;
            var replay = false;
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            foreach (var file in Directory.EnumerateFiles(directory, "*.nonce"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return ReplayClaim.Unavailable;
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (input.Length != sizeof(long)) return ReplayClaim.Unavailable;
                input.ReadExactly(bytes);
                var expires = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                input.Dispose();
                if (expires < now) { File.Delete(file); continue; }
                count++;
                if (Path.GetFileName(file).Equals(name, StringComparison.Ordinal)) replay = true;
                if (count > capacity) return ReplayClaim.CapacityExceeded;
            }
            if (replay) return ReplayClaim.Replay;
            if (count >= capacity) return ReplayClaim.CapacityExceeded;
            activity?.SetTag("scan.ms", timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            // Both legacy protocols allow at most 300 seconds of future/past skew.
            // Retain for the full possible validity period even if options change.
            var expiration = checked(now + 601);
            cancellationToken.ThrowIfCancellationRequested();
            pending = new FileStream(Path.Combine(directory, name), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, sizeof(long), FileOptions.None);
            Span<byte> value = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(value, expiration);
            pending.Write(value);
            pending.Flush(flushToDisk: false);
            activity?.SetTag("persist.ms", timer.Elapsed.TotalMilliseconds);
            return ReplayClaim.Accepted;
    }

}
