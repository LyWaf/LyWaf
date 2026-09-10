using System.Globalization;

namespace LyWaf.Control;

/// <summary>耗时资源列表的业务筛选条件，在分页与总数计算前执行。</summary>
internal sealed record TimingListFilter(string? Method, string? Backend, double? Requests,
    string RequestOperator, double? Average, string AverageOperator)
{
    public bool Matches(string method, string backend, long requests, double average) =>
        (Method == null || method.Equals(Method, StringComparison.OrdinalIgnoreCase)) &&
        (Backend == null || backend.Equals(Backend, StringComparison.OrdinalIgnoreCase)) &&
        (Requests == null || (RequestOperator == ">" ? requests > Requests : requests < Requests)) &&
        (Average == null || (AverageOperator == ">" ? average > Average : average < Average));

    public static TimingListFilter Parse(IQueryCollection query)
    {
        var method = Text("method", 32);
        if (method != null && !method.All(character => char.IsAsciiLetter(character)))
            throw new ArgumentException("HTTP 方法格式无效", "method");
        return new(method, Text("backend", 4096), Number("requestCount"), Operator("requestOp"), Number("avgTime"), Operator("avgTimeOp"));

        string? Text(string name, int length)
        {
            if (!query.TryGetValue(name, out var values)) return null;
            if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || values[0]!.Length > length)
                throw new ArgumentException("筛选值无效", name);
            return values[0];
        }
        double? Number(string name)
        {
            var text = Text(name, 64);
            if (text == null) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0)
                throw new ArgumentException("筛选阈值必须为非负有限数值", name);
            return value;
        }
        string Operator(string name)
        {
            var value = Text(name, 1) ?? ">";
            if (value is not (">" or "<")) throw new ArgumentException("比较符必须为 > 或 <", name);
            return value;
        }
    }
}
