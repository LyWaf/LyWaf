using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementListTests
{
    private sealed record Row(int Id, string Name, DateTimeOffset Time);
    private static ManagementList<Row> List() => ManagementLists.From(Enumerable.Range(1, 30)
        .Select(i => new Row(i, i % 2 == 0 ? "even" : "odd", DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddDays(i))))
        .Sort("id", row => row.Id).Search(row => row.Name).Timestamp(row => row.Time);

    [Fact]
    public void FiltersBeforeCountingAndPagingAndSortsNumerically()
    {
        var page = List().Read(Query("?search=EVEN&page=2&pageSize=3&sortDirection=desc&from=2026-01-10T00:00:00Z"), "id");
        Assert.Equal(11, page.Total);
        Assert.Equal(new[] { 24, 22, 20 }, page.Data.Select(row => row.Id));
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?sortBy=password", "sortBy")]
    [InlineData("?sortDirection=sideways", "sortDirection")]
    [InlineData("?from=invalid", "from")]
    [InlineData("?page=1&page=2", "page")]
    [InlineData("?from=2026-02-01&to=2026-01-01", "from")]
    public void InvalidQueriesIdentifyTheField(string query, string field) =>
        Assert.Equal(field, Assert.Throws<ArgumentException>(() => List().Read(Query(query), "id")).ParamName);

    [Fact]
    public void LargePageDoesNotOverflow() => Assert.Empty(List().Read(Query("?page=2147483647&pageSize=100"), "id").Data);

    private static IQueryCollection Query(string query)
    {
        var context = new DefaultHttpContext(); context.Request.QueryString = new QueryString(query); return context.Request.Query;
    }
}
