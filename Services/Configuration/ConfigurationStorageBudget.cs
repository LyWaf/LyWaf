using System.Text;

namespace LyWaf.Services.Configuration;

// Callers hold their writer gate across this check and the atomic replacement.
internal static class ConfigurationStorageBudget
{
    internal const long ContentBytes = 4 * 1024 * 1024;
    internal const long PreparedBytes = 64 * 1024 * 1024;
    internal const long HistoryBytes = 256 * 1024 * 1024;
    internal const long IndexBytes = 16 * 1024 * 1024;

    internal static void CheckContent(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > ContentBytes)
            throw new IOException("Configuration content exceeds 4 MiB capacity");
    }

    internal static void Reserve(string directory, long bytes, int files, long maximumBytes, int maximumFiles)
    {
        long used = 0;
        var count = 0;
        if (Directory.Exists(directory))
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Configuration storage cannot contain links");
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Configuration storage cannot contain links");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else
                    {
                        used = checked(used + new FileInfo(entry).Length);
                        count++;
                    }
                    if (used > maximumBytes - bytes || count > maximumFiles - files)
                        throw new IOException("Configuration storage capacity requires operator maintenance");
                }
            }
        }
        if (bytes < 0 || used > maximumBytes - bytes || count > maximumFiles - files)
            throw new IOException("Configuration storage capacity requires operator maintenance");
    }
}
