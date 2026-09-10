using DuckDB.NET.Data;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementSqlListTests
{
    private sealed record Row(int Id, string Name);
    private static readonly Dictionary<string, string> Sorts = new() { ["id"] = "id", ["name"] = "name" };

    [Fact]
    public void FiltersInDatabaseAndCountsBeforePaging()
    {
        using var connection = Database();
        var query = Parse("?search=alpha&from=2026-01-01T00:00:00Z&to=2026-01-03T00:00:00Z&page=2&pageSize=1&sortBy=id&sortDirection=desc");
        var result = Read(connection, query);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, Assert.Single(result.Data).Id);
        Assert.Equal(2, result.Page);
        var empty = Read(connection, query with { Page = int.MaxValue });
        Assert.Equal(2, empty.Total);
        Assert.Empty(empty.Data);
    }

    [Theory]
    [InlineData("' OR 1=1 --", 0)]
    [InlineData("%", 1)]
    [InlineData("ALPHA", 2)]
    public void SearchIsParameterizedLiteralSubstring(string search, int count)
    {
        using var connection = Database();
        var result = Read(connection, Parse("?search=" + Uri.EscapeDataString(search)));
        Assert.Equal(count, result.Total);
        Assert.Equal(count, result.Data.Length);
    }

    private static ManagementPage<Row> Read(DuckDBConnection connection, ManagementListQuery query)
        => ManagementSqlList.Read(connection, query, "entries", "id, name", Sorts, "name", "created_at",
            reader => new Row(reader.GetInt32(0), reader.GetString(1)));

    private static ManagementListQuery Parse(string query)
        => ManagementListQuery.Parse(new QueryCollection(QueryHelpers.ParseQuery(query)), Sorts.Keys, "id");

    private static DuckDBConnection Database()
    {
        var connection = new DuckDBConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE entries(id INTEGER, name VARCHAR, created_at TIMESTAMP); INSERT INTO entries VALUES (1, 'alpha', '2026-01-01'), (2, 'alpha%', '2026-01-02'), (3, 'beta', '2026-01-04');";
        command.ExecuteNonQuery();
        return connection;
    }
}
