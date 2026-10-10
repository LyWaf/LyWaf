using LyWaf.Plugins.Sys.HttpUpload;

namespace LyWaf.Services.Files;

/// <summary>Replaces a physical directory using a sibling version backup and rollback, not a pointer.</summary>
public static class DirectoryPublication
{
    private static readonly object Gate = new();
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Dictionary<string, int> Readers = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly HashSet<string> Publishing = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static bool IsInternalName(string name) => name.StartsWith(".lywaf-", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith('.') && name.EndsWith("_version", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string parent, string child) => parent.Equals(child, Comparison)
        || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, Comparison);
    private static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);

    public sealed class Lease(string path) : IDisposable
    {
        public string Path { get; } = path;
        private bool disposed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (disposed) return;
                disposed = true;
                if (--Readers[Path] == 0) Readers.Remove(Path);
                Monitor.PulseAll(Gate);
            }
        }
    }

    public static Lease Acquire(string directory)
    {
        lock (Gate)
        {
            directory = Normalize(directory);
            while (Publishing.Any(path => Overlaps(path, directory))) Monitor.Wait(Gate);
            Readers[directory] = Readers.GetValueOrDefault(directory) + 1;
            return new Lease(directory);
        }
    }

    public static void Publish(string root, string destination, string unpacked)
    {
        root = Normalize(root);
        destination = Normalize(destination);
        unpacked = Normalize(unpacked);
        var relative = Path.GetRelativePath(root, destination);
        if (!Contains(root, destination) || root.Equals(destination, Comparison)
            || relative.Split(Path.DirectorySeparatorChar).Any(p => !HttpUploadMiddleware.IsSafeName(p))
            || !Contains(root, unpacked) || Overlaps(destination, unpacked))
            throw new InvalidDataException("Publication must target a non-root child directory with separate staging.");
        lock (Gate)
        {
            while (Publishing.Any(path => Overlaps(path, destination))) Monitor.Wait(Gate);
            Publishing.Add(destination);
            try
            {
                var deadline = Environment.TickCount64 + 30000;
                while (Readers.Keys.Any(path => Overlaps(path, destination)))
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) throw new IOException("Static readers are busy; publication was not changed.");
                    Monitor.Wait(Gate, (int)remaining);
                }
                HttpUploadMiddleware.CheckDirectory(destination);
                ValidateTree(unpacked);
                if (File.Exists(destination)) throw new IOException("Publication target is a file.");
                var parent = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(parent);
                HttpUploadMiddleware.CheckDirectory(parent);
                var versions = Path.Combine(parent, "." + Path.GetFileName(destination) + "_version");
                HttpUploadMiddleware.CheckDirectory(versions);
                Directory.CreateDirectory(versions);
                var backup = Path.Combine(versions, Guid.NewGuid().ToString("N"));
                var moved = false;
                if (Directory.Exists(destination))
                {
                    ValidateTree(destination);
                    Directory.Move(destination, backup);
                    moved = true;
                }
                try { Directory.Move(unpacked, destination); }
                catch
                {
                    if (moved) Directory.Move(backup, destination);
                    throw;
                }
                // Keep only the immediately preceding version for recovery. Never delete unknown entries.
                foreach (var old in Directory.EnumerateDirectories(versions))
                {
                    if (old.Equals(backup, Comparison) || !Guid.TryParseExact(Path.GetFileName(old), "N", out _)) continue;
                    try { ValidateTree(old); Directory.Delete(old, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Old upload backup cleanup failed"); }
                }
            }
            finally { Publishing.Remove(destination); Monitor.PulseAll(Gate); }
        }
    }

    private static void ValidateTree(string path)
    {
        HttpUploadMiddleware.CheckDirectory(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked publication entries are not allowed.");
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }
}
