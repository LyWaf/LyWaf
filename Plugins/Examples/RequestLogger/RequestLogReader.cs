using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LyWaf.Utils;

namespace LyWaf.Plugins.Examples.RequestLogger;

internal static class RequestLogReader
{
    internal static async Task<(List<LogEntry> Entries, int Total)> ReadJsonAsync(
        FileStream snapshot, long length, int offset, int limit, string search, CancellationToken token)
    {
        var page = new List<(LogEntry Entry, int ReverseIndex)>();
        var total = 0;
        var physicalLines = 0;
        long pageBytes = 0;
        await foreach (var line in ReverseLinesAsync(snapshot, length, token))
        {
            var reverseIndex = physicalLines++;
            if (!line.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            RequestLogRecord? record;
            try { record = JsonSerializer.Deserialize<RequestLogRecord>(line, RequestLogRecord.Json); }
            catch (JsonException) { continue; }
            if (record is null) continue;
            if (total++ < offset || page.Count >= limit) continue;
            pageBytes += Encoding.UTF8.GetByteCount(line);
            if (pageBytes > 8 * 1024 * 1024) throw new ArgumentException("Requested log page is too large");
            page.Add((record.ToEntry(0, line), reverseIndex));
        }
        foreach (var item in page) item.Entry.Index = physicalLines - item.ReverseIndex - 1;
        return (page.Select(item => item.Entry).ToList(), total);
    }

    // Read a pinned prefix backwards, without loading the complete file or decoding split UTF-8 fragments.
    private static async IAsyncEnumerable<string> ReverseLinesAsync(FileStream stream, long length,
        [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[65536];
        var fragments = new List<byte[]>();
        var recordBytes = 0;
        var end = length;
        while (end > 0)
        {
            token.ThrowIfCancellationRequested();
            var start = Math.Max(0, end - buffer.Length);
            var count = (int)(end - start);
            stream.Seek(start, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), token);
            var segmentEnd = count;
            for (var i = count - 1; i >= 0; i--)
            {
                if (buffer[i] != '\n') continue;
                if (end == length && i == count - 1) { segmentEnd = i; continue; }
                Add(i + 1, segmentEnd);
                yield return Finish(false);
                segmentEnd = i;
            }
            Add(0, segmentEnd);
            end = start;
        }
        if (length > 0) yield return Finish(true);

        void Add(int start, int end)
        {
            recordBytes += end - start;
            if (recordBytes > 8 * 1024 * 1024) throw new ArgumentException("Log record is too large");
            if (end > start) fragments.Add(buffer[start..end]);
        }
        string Finish(bool first)
        {
            var bytes = new byte[recordBytes];
            var position = 0;
            for (var i = fragments.Count - 1; i >= 0; i--)
            { fragments[i].CopyTo(bytes, position); position += fragments[i].Length; }
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\r');
            fragments.Clear(); recordBytes = 0;
            return first ? text.TrimStart('\ufeff') : text;
        }
    }
}
