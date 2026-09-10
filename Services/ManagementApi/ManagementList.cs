using System.Globalization;

namespace LyWaf.Services.ManagementApi;

/// <summary>管理列表公共查询结果，Total 为过滤后、分页前的数量。</summary>
public record ManagementPage<T>(bool Success, T[] Data, int Total, int Page, int PageSize, string SortBy, string SortDirection);

public static class ManagementLists
{
    public static ManagementList<T> From<T>(IEnumerable<T> source) => new(source);
}

/// <summary>按业务显式声明可排序、搜索和时间字段，禁止客户端反射访问任意属性。</summary>
public sealed class ManagementList<T>(IEnumerable<T> source)
{
    private readonly Dictionary<string, Func<T, object?>> sorts = new(StringComparer.Ordinal);
    private Func<T, string?>? search;
    private Func<T, DateTimeOffset?>? timestamp;

    public ManagementList<T> Sort(string field, Func<T, object?> selector) { sorts.Add(field, selector); return this; }
    public ManagementList<T> Search(Func<T, string?> selector) { search = selector; return this; }
    public ManagementList<T> Timestamp(Func<T, DateTimeOffset?> selector) { timestamp = selector; return this; }

    public ManagementPage<T> Read(IQueryCollection query, string defaultSort, bool descending = false)
        => Read(ManagementListQuery.Parse(query, sorts.Keys, defaultSort, descending, search != null, timestamp != null));

    public ManagementPage<T> Read(ManagementListQuery options)
    {
        var key = sorts[options.SortBy];
        var filtered = source.Where(item => (options.Search.Length == 0 || (search!(item)?.Contains(options.Search, StringComparison.OrdinalIgnoreCase) ?? false)) &&
            (options.From == null || timestamp!(item) >= options.From) && (options.To == null || timestamp!(item) <= options.To)).ToArray();
        var comparer = Comparer<object?>.Create((left, right) =>
            left is string a && right is string b ? StringComparer.OrdinalIgnoreCase.Compare(a, b) : Comparer<object?>.Default.Compare(left, right));
        var ordered = options.SortDirection == "desc" ? filtered.OrderByDescending(key, comparer) : filtered.OrderBy(key, comparer);
        return options.Result(options.Offset >= filtered.Length ? [] : ordered.Skip((int)options.Offset).Take(options.PageSize).ToArray(), filtered.Length);
    }
}

/// <summary>内存及数据库列表共用的已验证查询参数，字段名由各业务白名单提供。</summary>
public sealed record ManagementListQuery(int Page, int PageSize, string SortBy, string SortDirection,
    string Search, DateTimeOffset? From, DateTimeOffset? To)
{
    public long Offset => (long)(Page - 1) * PageSize;
    public ManagementPage<T> Result<T>(T[] data, int total) => new(true, data, total, Page, PageSize, SortBy, SortDirection);

    public static ManagementListQuery Parse(IQueryCollection query, IEnumerable<string> sorts, string defaultSort,
        bool descending = false, bool searchSupported = true, bool timeSupported = true)
    {
        var page = Number("page", 1, int.MaxValue);
        var size = Number("pageSize", 20, 100);
        var sort = Value("sortBy", defaultSort);
        if (!sorts.Contains(sort, StringComparer.Ordinal)) throw new ArgumentException("排序字段不支持", "sortBy");
        var direction = Value("sortDirection", descending ? "desc" : "asc");
        if (direction is not "asc" and not "desc") throw new ArgumentException("排序方向须为 asc 或 desc", "sortDirection");
        var text = Value("search", "");
        if (text.Length > 200) throw new ArgumentException("搜索文本不能超过 200 字符", "search");
        if (text.Length > 0 && !searchSupported) throw new ArgumentException("此列表不支持文本搜索", "search");
        var from = Time("from"); var to = Time("to");
        if (from > to) throw new ArgumentException("开始时间不能晚于结束时间", "from");
        if ((from != null || to != null) && !timeSupported) throw new ArgumentException("此列表没有时间字段", "from");
        return new(page, size, sort, direction, text, from, to);

        string Value(string name, string fallback)
        {
            if (!query.TryGetValue(name, out var value)) return fallback;
            if (value.Count != 1) throw new ArgumentException("查询参数不能重复", name);
            return value[0] ?? "";
        }
        int Number(string name, int fallback, int maximum)
        {
            var value = Value(name, fallback.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > maximum)
                throw new ArgumentException($"参数须在 1 至 {maximum} 之间", name);
            return number;
        }
        DateTimeOffset? Time(string name)
        {
            var value = Value(name, "");
            if (value.Length == 0) return null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                throw new ArgumentException("时间格式无效", name);
            return parsed;
        }
    }
}
