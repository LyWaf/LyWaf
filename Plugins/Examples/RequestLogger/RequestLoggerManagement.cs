using System.Text;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Examples.RequestLogger;
using LyWaf.Utils;

namespace LyWaf.Plugins.Examples;

public partial class RequestLoggerPlugin
{
    public override IReadOnlyList<PluginAction> GetActions() =>
    [new() { Id = "view-logs", Name = "查看日志", Type = "log-viewer", Description = "查看请求日志文件，支持分页和搜索" }];

    public override async Task<PluginActionResult> ExecuteActionAsync(string actionId, Dictionary<string, string>? parameters)
    {
        if (actionId != "view-logs") return Error("Unknown request logger action");
        parameters ??= [];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var action = parameters.GetValueOrDefault("action", "list-files");
            if (action == "status") return new() { Data = new { type = "writer-status", statistics = WriterStatus } };
            if (_writer is { } writer) await writer.FlushAsync(timeout.Token);
            if (action == "read-entries")
            {
                var name = parameters.GetValueOrDefault("file", "");
                var snapshot = await OpenSnapshotAsync(name, timeout.Token);
                await using var stream = snapshot.Stream;
                return await ReadEntriesAsync(stream, snapshot.Length, name, parameters, timeout.Token);
            }
            await _fileLock.WaitAsync(timeout.Token);
            try
            {
                var directory = GetLogDirectory();
                RequestLogWriter.EnsureSafePath(directory);
                if (action == "list-files")
                    return new() { Data = new { type = "file-list", files = Directory.Exists(directory)
                        ? LogUtil.ListLogFiles(directory).Concat(LogUtil.ListLogFiles(directory, "*.jsonl"))
                            .Where(file => IsOwnedLogFile(file.Name) && (File.GetAttributes(Path.Combine(directory, file.Name)) & FileAttributes.ReparsePoint) == 0)
                            .OrderByDescending(file => file.ModifiedAt).ToArray() : [], statistics = WriterStatus } };
                var name = parameters.GetValueOrDefault("file", "");
                if (!IsOwnedLogFile(name)) return Error("Invalid request log file name");
                var path = Path.Combine(directory, name);
                RequestLogWriter.EnsureSafePath(path);
                if (!File.Exists(path)) return Error("Request log file not found");
                switch (action)
                {
                    case "delete-file": File.Delete(path); return new();
                    case "clear-file": await ReplaceWithEmptyAsync(path, timeout.Token); return new();
                    case "delete-entry":
                        if (!int.TryParse(parameters.GetValueOrDefault("index"), out var index) || index < 0) return Error("Invalid log entry index");
                        var deleted = name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                            ? await DeleteJsonEntryAsync(path, index, timeout.Token) : await LogUtil.DeleteLogEntryAsync(path, index);
                        return deleted ? new() : Error("Log entry index not found");
                    default: return Error("Unknown request logger action");
                }
            }
            finally { _fileLock.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { return Error("Request log operation unavailable (" + error.GetType().Name + ")"); }
    }

    private static PluginActionResult Error(string message) => new() { Success = false, Message = message };
    private string GetLogDirectory()
    {
        var path = Path.GetFullPath(Path.IsPathRooted(Options.LogDirectory) ? Options.LogDirectory
            : Path.Combine(AppContext.BaseDirectory, Options.LogDirectory));
        if (Path.GetPathRoot(path) == path) throw new ArgumentException("Log directory cannot be a filesystem root");
        return path;
    }

    internal async Task<(FileStream Stream, long Length)> OpenSnapshotAsync(string name, CancellationToken token)
    {
        if (!IsOwnedLogFile(name)) throw new ArgumentException("Invalid request log file name");
        await _fileLock.WaitAsync(token);
        try
        {
            var path = Path.Combine(GetLogDirectory(), name);
            RequestLogWriter.EnsureSafePath(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try { return (stream, stream.Length); }
            catch { await stream.DisposeAsync(); throw; }
        }
        finally { _fileLock.Release(); }
    }

    private static async Task<PluginActionResult> ReadEntriesAsync(FileStream stream, long length, string name,
        Dictionary<string, string> parameters, CancellationToken token)
    {
        _ = int.TryParse(parameters.GetValueOrDefault("offset", "0"), out var offset);
        _ = int.TryParse(parameters.GetValueOrDefault("limit", "20"), out var limit);
        offset = Math.Clamp(offset, 0, 10000000); limit = Math.Clamp(limit, 1, 100);
        List<LogEntry> entries; int total;
        var search = parameters.GetValueOrDefault("search", "");
        if (search.Length > 1024) return Error("Search is too long");
        if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            (entries, total) = await RequestLogReader.ReadJsonAsync(stream, length, offset, limit, search, token);
        }
        else
        {
            using var scanner = new ReverseLogScanner(stream, length);
            (entries, total) = await scanner.QuerySnapshotAsync(offset, limit, search, token);
        }
        return new() { Data = new { type = "entries", entries, total, offset, limit,
            fileName = name, fileSize = length } };
    }

    private static async Task ReplaceWithEmptyAsync(string path, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options)) { await file.FlushAsync(token); }
            token.ThrowIfCancellationRequested();
            File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async IAsyncEnumerable<string> ReadLogLinesAsync(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var line = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                else line.Append(buffer[i]);
                if (line.Length > 8 * 1024 * 1024) throw new ArgumentException("Log record is too large");
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static async Task<bool> DeleteJsonEntryAsync(string path, int index, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var found = false;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var reader = new StreamReader(path))
            await using (var writer = new StreamWriter(new FileStream(temporary, options), new UTF8Encoding(false)))
            {
                var position = 0;
                await foreach (var line in ReadLogLinesAsync(reader, token))
                    if (position++ == index) found = true; else await writer.WriteLineAsync(line.AsMemory(), token);
            }
            token.ThrowIfCancellationRequested();
            if (found) File.Replace(temporary, path, null);
            return found;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
