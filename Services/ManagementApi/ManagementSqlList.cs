using System.Data.Common;

namespace LyWaf.Services.ManagementApi;

/// <summary>固定业务 SQL 白名单加参数化过滤；只物化当前页，不加载整张日志表。</summary>
internal static class ManagementSqlList
{
    public static ManagementPage<T> Read<T>(DbConnection connection, ManagementListQuery query,
        string table, string columns, IReadOnlyDictionary<string, string> sorts, string searchExpression,
        string timeColumn, Func<DbDataReader, T> map, string? tieBreaker = null)
    {
        if (!sorts.TryGetValue(query.SortBy, out var sort) || query.SortDirection is not ("asc" or "desc") || query.Page < 1 || query.PageSize is < 1 or > 100)
            throw new ArgumentException("列表查询参数无效");
        var parameters = new List<object>();
        var filters = new List<string>();
        if (query.Search.Length > 0) filters.Add($"contains(lower({searchExpression}), lower({Add(query.Search)}))");
        if (query.From != null) filters.Add($"{timeColumn} >= {Add(query.From.Value.UtcDateTime)}");
        if (query.To != null) filters.Add($"{timeColumn} <= {Add(query.To.Value.UtcDateTime)}");
        var where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
        using var count = Command($"SELECT COUNT(*) FROM {table}{where}");
        var total = checked(Convert.ToInt32(count.ExecuteScalar()));
        var limit = Add(query.PageSize); var offset = Add(query.Offset);
        var order = $"{sort} {query.SortDirection}" + (tieBreaker == null ? "" : $", {tieBreaker} {query.SortDirection}");
        using var command = Command($"SELECT {columns} FROM {table}{where} ORDER BY {order} LIMIT {limit} OFFSET {offset}");
        using var reader = command.ExecuteReader();
        var items = new List<T>(query.PageSize);
        while (reader.Read()) items.Add(map(reader));
        return query.Result(items.ToArray(), total);

        string Add(object value) { parameters.Add(value); return "$" + parameters.Count; }
        DbCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter(); parameter.Value = value; command.Parameters.Add(parameter);
            }
            return command;
        }
    }
}
