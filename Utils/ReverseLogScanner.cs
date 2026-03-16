using System.Collections.Concurrent;
using System.Text;

namespace LyWaf.Utils;

/// <summary>
/// 高性能日志文件扫描器，避免将整个文件加载到内存。
/// 通过前向扫描建立分隔符索引，按需读取条目内容。
/// </summary>
public sealed class ReverseLogScanner : IDisposable
{
    private const int BufferSize = 65536; // 64KB
    private static readonly byte[] SeparatorPattern = "========== ["u8.ToArray();

    private readonly FileStream _stream;
    private readonly long _fileLength;
    private List<(long ByteOffset, string Timestamp)>? _index;

    public ReverseLogScanner(string filePath)
    {
        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true);
        _fileLength = _stream.Length;
    }

    /// <summary>
    /// 分页查询日志条目（倒序：最新在前，支持搜索和时间过滤）
    /// </summary>
    public async Task<(List<LogEntry> entries, int total)> QueryAsync(
        int offset = 0, int limit = 20, string? search = null,
        DateTime? startTime = null, DateTime? endTime = null)
    {
        if (_fileLength == 0)
            return ([], 0);

        var index = await BuildIndexAsync();
        if (index.Count == 0)
            return ([], 0);

        var hasSearch = !string.IsNullOrEmpty(search);
        var hasTimeFilter = startTime.HasValue || endTime.HasValue;

        // 无过滤：直接在索引上分页，最快路径
        if (!hasSearch && !hasTimeFilter)
        {
            var total = index.Count;
            var entries = await ReadPageReversedAsync(index, 0, index.Count - 1, offset, limit);
            return (entries, total);
        }

        // 有时间过滤：二分查找范围
        int rangeStart = 0, rangeEnd = index.Count - 1;
        if (hasTimeFilter)
        {
            (rangeStart, rangeEnd) = FindTimeRange(index, startTime, endTime);
            if (rangeStart > rangeEnd)
                return ([], 0);
        }

        // 无搜索，仅时间过滤
        if (!hasSearch)
        {
            var total = rangeEnd - rangeStart + 1;
            var entries = await ReadPageReversedAsync(index, rangeStart, rangeEnd, offset, limit);
            return (entries, total);
        }

        // 有搜索过滤：需逐条检查内容
        return await SearchAndPageAsync(index, rangeStart, rangeEnd, offset, limit, search!);
    }

    /// <summary>
    /// 快速统计日志条目总数
    /// </summary>
    public async Task<int> CountEntriesAsync()
    {
        if (_fileLength == 0) return 0;
        var index = await BuildIndexAsync();
        return index.Count;
    }

    /// <summary>
    /// 删除指定索引（前向索引）的条目
    /// </summary>
    public async Task<bool> DeleteEntryAsync(string filePath, int entryIndex)
    {
        var index = await BuildIndexAsync();
        if (entryIndex < 0 || entryIndex >= index.Count)
            return false;

        var startByte = index[entryIndex].ByteOffset;
        var endByte = (entryIndex + 1 < index.Count) ? index[entryIndex + 1].ByteOffset : _fileLength;

        // 读取删除前后的内容，避免全文件加载到单个字符串
        Dispose(); // 关闭读取流

        // 使用临时文件进行安全写入
        var tempPath = filePath + ".tmp";
        await using (var reader = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize))
        await using (var writer = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            // 复制删除点之前的内容
            if (startByte > 0)
            {
                await CopyBytesAsync(reader, writer, startByte);
            }

            // 跳过被删除的条目
            reader.Seek(endByte, SeekOrigin.Begin);

            // 复制删除点之后的内容
            var remaining = reader.Length - endByte;
            if (remaining > 0)
            {
                await CopyBytesAsync(reader, writer, remaining);
            }
        }

        File.Move(tempPath, filePath, overwrite: true);
        IndexCache.TryRemove(filePath, out _); // 使缓存失效
        return true;
    }

    /// <summary>
    /// 建立分隔符索引：扫描文件中所有 ========== [timestamp] ========== 的位置
    /// </summary>
    private async Task<List<(long ByteOffset, string Timestamp)>> BuildIndexAsync()
    {
        if (_index != null) return _index;

        var index = new List<(long ByteOffset, string Timestamp)>();
        var buffer = new byte[BufferSize];
        var carryOver = Array.Empty<byte>(); // 上一块的尾部（处理跨块的分隔符）
        long fileOffset = 0;

        _stream.Seek(0, SeekOrigin.Begin);

        // 跳过 UTF-8 BOM
        if (_fileLength >= 3)
        {
            var bom = new byte[3];
            await _stream.ReadExactlyAsync(bom);
            if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                fileOffset = 3;
            }
            else
            {
                _stream.Seek(0, SeekOrigin.Begin);
            }
        }

        while (true)
        {
            var bytesRead = await _stream.ReadAsync(buffer);
            if (bytesRead == 0) break;

            // 合并跨块数据
            byte[] searchData;
            int searchOffset;
            if (carryOver.Length > 0)
            {
                searchData = new byte[carryOver.Length + bytesRead];
                carryOver.CopyTo(searchData, 0);
                buffer.AsSpan(0, bytesRead).CopyTo(searchData.AsSpan(carryOver.Length));
                searchOffset = -carryOver.Length;
            }
            else
            {
                searchData = buffer;
                searchOffset = 0;
            }

            var searchSpan = searchData.AsSpan(0, carryOver.Length + bytesRead);

            // 在当前数据中查找所有分隔符
            int pos = 0;
            while (pos < searchSpan.Length)
            {
                var remaining = searchSpan[pos..];
                var found = remaining.IndexOf(SeparatorPattern);
                if (found < 0) break;

                var matchPos = pos + found;
                var absolutePos = fileOffset + matchPos + searchOffset;

                // 确保分隔符在行首（前一个字符是换行或文件开头）
                if (absolutePos == 0 || (matchPos > 0 && (searchData[matchPos - 1] == '\n')))
                {
                    // 提取时间戳：找到 [ 和 ] 之间的文本
                    var bracketStart = matchPos + SeparatorPattern.Length - 1; // '[' 的位置
                    var timestamp = ExtractTimestamp(searchData, bracketStart, carryOver.Length + bytesRead);
                    if (timestamp != null)
                    {
                        index.Add((absolutePos, timestamp));
                    }
                }

                pos = matchPos + SeparatorPattern.Length;
            }

            // 保留尾部以处理跨块分隔符
            var keepBytes = Math.Min(SeparatorPattern.Length + 64, bytesRead); // 64 extra for timestamp
            carryOver = new byte[keepBytes];
            Array.Copy(buffer, bytesRead - keepBytes, carryOver, 0, keepBytes);

            fileOffset += bytesRead;
        }

        _index = index;
        return index;
    }

    /// <summary>
    /// 从字节数据中提取时间戳（ [ 和 ] 之间的内容）
    /// </summary>
    private static string? ExtractTimestamp(byte[] data, int bracketPos, int dataLength)
    {
        if (bracketPos >= dataLength || data[bracketPos] != (byte)'[')
            return null;

        var endBracket = -1;
        for (int i = bracketPos + 1; i < dataLength && i < bracketPos + 40; i++)
        {
            if (data[i] == (byte)']')
            {
                endBracket = i;
                break;
            }
        }

        if (endBracket < 0) return null;

        return Encoding.UTF8.GetString(data, bracketPos + 1, endBracket - bracketPos - 1).Trim();
    }

    /// <summary>
    /// 从索引范围中倒序读取指定页的条目
    /// </summary>
    private async Task<List<LogEntry>> ReadPageReversedAsync(
        List<(long ByteOffset, string Timestamp)> index,
        int rangeStart, int rangeEnd,
        int offset, int limit)
    {
        var entries = new List<LogEntry>();
        var rangeCount = rangeEnd - rangeStart + 1;

        // 倒序遍历：最新在前
        var startIdx = rangeEnd - offset;
        var count = 0;

        for (int i = startIdx; i >= rangeStart && count < limit; i--, count++)
        {
            var block = await ReadEntryBlockAsync(index, i);
            var entry = LogUtil.ParseLogEntry(block, i, index[i].Timestamp);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// 搜索过滤模式：逐条读取内容进行匹配，并分页
    /// </summary>
    private async Task<(List<LogEntry> entries, int total)> SearchAndPageAsync(
        List<(long ByteOffset, string Timestamp)> index,
        int rangeStart, int rangeEnd,
        int offset, int limit, string search)
    {
        var entries = new List<LogEntry>();
        var matchCount = 0;
        var collected = 0;

        // 倒序扫描
        for (int i = rangeEnd; i >= rangeStart; i--)
        {
            var block = await ReadEntryBlockAsync(index, i);

            if (!block.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            matchCount++;

            if (matchCount > offset && collected < limit)
            {
                var entry = LogUtil.ParseLogEntry(block, i, index[i].Timestamp);
                entries.Add(entry);
                collected++;
            }
        }

        return (entries, matchCount);
    }

    /// <summary>
    /// 读取单个条目的文本块（从当前分隔符到下一个分隔符之间的内容）
    /// </summary>
    private async Task<string> ReadEntryBlockAsync(
        List<(long ByteOffset, string Timestamp)> index, int entryIdx)
    {
        var startByte = index[entryIdx].ByteOffset;
        var endByte = (entryIdx + 1 < index.Count) ? index[entryIdx + 1].ByteOffset : _fileLength;
        var length = (int)(endByte - startByte);

        var buffer = new byte[length];
        _stream.Seek(startByte, SeekOrigin.Begin);
        var read = await _stream.ReadAsync(buffer.AsMemory(0, length));

        return Encoding.UTF8.GetString(buffer, 0, read).TrimEnd();
    }

    /// <summary>
    /// 二分查找时间范围在索引中的起止位置
    /// </summary>
    private static (int start, int end) FindTimeRange(
        List<(long ByteOffset, string Timestamp)> index,
        DateTime? startTime, DateTime? endTime)
    {
        int rangeStart = 0, rangeEnd = index.Count - 1;

        if (startTime.HasValue)
        {
            // 找到第一个 >= startTime 的位置
            rangeStart = LowerBound(index, startTime.Value);
        }

        if (endTime.HasValue)
        {
            // 找到最后一个 <= endTime 的位置
            rangeEnd = UpperBound(index, endTime.Value);
        }

        return (rangeStart, rangeEnd);
    }

    private static int LowerBound(List<(long ByteOffset, string Timestamp)> index, DateTime target)
    {
        int lo = 0, hi = index.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (DateTime.TryParse(index[mid].Timestamp, out var t) && t < target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBound(List<(long ByteOffset, string Timestamp)> index, DateTime target)
    {
        int lo = 0, hi = index.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (DateTime.TryParse(index[mid].Timestamp, out var t) && t <= target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo - 1;
    }

    private static async Task CopyBytesAsync(Stream source, Stream dest, long count)
    {
        var buffer = new byte[BufferSize];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, BufferSize);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead));
            if (read == 0) break;
            await dest.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    #region 索引缓存

    private static readonly ConcurrentDictionary<string, CachedIndex> IndexCache = new();

    /// <summary>
    /// 带缓存的索引构建：如果文件未变化则复用索引，如果文件增长则增量扫描
    /// </summary>
    public static async Task<List<(long ByteOffset, string Timestamp)>> GetCachedIndexAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        var fileInfo = new FileInfo(filePath);
        var fileLength = fileInfo.Length;

        if (IndexCache.TryGetValue(filePath, out var cached))
        {
            // 文件未变化，直接返回缓存
            if (cached.FileLength == fileLength)
                return cached.Entries;

            // 文件缩小（被清空或删除条目），重建
            if (fileLength < cached.FileLength)
            {
                IndexCache.TryRemove(filePath, out _);
            }
            else
            {
                // 文件增长，增量扫描新增部分
                var newEntries = await ScanFromOffsetAsync(filePath, cached.FileLength, fileLength);
                var merged = new List<(long ByteOffset, string Timestamp)>(cached.Entries.Count + newEntries.Count);
                merged.AddRange(cached.Entries);
                merged.AddRange(newEntries);

                var updated = new CachedIndex { FileLength = fileLength, Entries = merged };
                IndexCache[filePath] = updated;
                return merged;
            }
        }

        // 全量构建
        using var scanner = new ReverseLogScanner(filePath);
        var index = await scanner.BuildIndexAsync();

        IndexCache[filePath] = new CachedIndex { FileLength = fileLength, Entries = index };
        return index;
    }

    /// <summary>
    /// 使指定文件的缓存失效
    /// </summary>
    public static void InvalidateCache(string filePath)
    {
        IndexCache.TryRemove(filePath, out _);
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    public static void ClearCache()
    {
        IndexCache.Clear();
    }

    /// <summary>
    /// 从指定偏移量开始扫描新增的分隔符
    /// </summary>
    private static async Task<List<(long ByteOffset, string Timestamp)>> ScanFromOffsetAsync(
        string filePath, long fromOffset, long fileLength)
    {
        var index = new List<(long ByteOffset, string Timestamp)>();
        var buffer = new byte[BufferSize];

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, true);

        // 回退一点以处理跨块的分隔符
        var seekTo = Math.Max(0, fromOffset - SeparatorPattern.Length - 64);
        stream.Seek(seekTo, SeekOrigin.Begin);

        var carryOver = Array.Empty<byte>();
        long fileOffset = seekTo;

        while (stream.Position < fileLength)
        {
            var bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) break;

            byte[] searchData;
            int searchOffset;
            if (carryOver.Length > 0)
            {
                searchData = new byte[carryOver.Length + bytesRead];
                carryOver.CopyTo(searchData, 0);
                buffer.AsSpan(0, bytesRead).CopyTo(searchData.AsSpan(carryOver.Length));
                searchOffset = -(carryOver.Length);
            }
            else
            {
                searchData = buffer;
                searchOffset = 0;
            }

            var searchSpan = searchData.AsSpan(0, carryOver.Length + bytesRead);

            int pos = 0;
            while (pos < searchSpan.Length)
            {
                var remaining = searchSpan[pos..];
                var found = remaining.IndexOf(SeparatorPattern);
                if (found < 0) break;

                var matchPos = pos + found;
                var absolutePos = fileOffset + matchPos + searchOffset;

                // 只收集真正新增的（>= fromOffset）
                if (absolutePos >= fromOffset &&
                    (absolutePos == 0 || (matchPos > 0 && searchData[matchPos - 1] == '\n')))
                {
                    var bracketStart = matchPos + SeparatorPattern.Length - 1;
                    var timestamp = ExtractTimestamp(searchData, bracketStart, carryOver.Length + bytesRead);
                    if (timestamp != null)
                    {
                        index.Add((absolutePos, timestamp));
                    }
                }

                pos = matchPos + SeparatorPattern.Length;
            }

            var keepBytes = Math.Min(SeparatorPattern.Length + 64, bytesRead);
            carryOver = new byte[keepBytes];
            Array.Copy(buffer, bytesRead - keepBytes, carryOver, 0, keepBytes);

            fileOffset += bytesRead;
        }

        return index;
    }

    private class CachedIndex
    {
        public long FileLength;
        public List<(long ByteOffset, string Timestamp)> Entries = [];
    }

    #endregion
}
