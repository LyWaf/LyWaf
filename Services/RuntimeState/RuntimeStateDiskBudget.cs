namespace LyWaf.Services.RuntimeState;

public sealed class RuntimeStateCapacityException(string budget) : IOException("Runtime state capacity exceeded: " + budget)
{
    public string Budget { get; } = budget;
}

internal static class RuntimeStateDiskBudget
{
    // Every runtime writer holds this cross-process lease until bytes are visible to subsequent scans.
    internal static FileStream Reserve(RuntimeStateOptions options, long additionalBytes, int additionalFiles = 0,
        CancellationToken token = default)
    {
        var root = Path.GetFullPath(options.StateDirectory);
        Directory.CreateDirectory(root);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        FileStream held;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                held = new FileStream(Path.Combine(root, ".runtime-budget.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                if (token.WaitHandle.WaitOne(10)) token.ThrowIfCancellationRequested();
            }
        }
        try
        {
            long bytes = additionalBytes;
            long count = additionalFiles;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes = checked(bytes + file.Length);
                if (file.Extension != ".lock") count++;
                if (bytes > options.MaxDiskBytes || count > options.MaxDiskFiles)
                    throw new RuntimeStateCapacityException("runtime-directory");
            }
            if (bytes > options.MaxDiskBytes || count > options.MaxDiskFiles)
                throw new RuntimeStateCapacityException("runtime-directory");
            return held;
        }
        catch { held.Dispose(); throw; }
    }
}
