using LyWaf.Control;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LyWaf.Tests;

public sealed class TimingListFilterTests
{
    [Theory]
    [InlineData("?requestCount=NaN", "requestCount")]
    [InlineData("?avgTime=-1", "avgTime")]
    [InlineData("?avgTime=Infinity", "avgTime")]
    [InlineData("?requestOp=eq", "requestOp")]
    [InlineData("?method=GET&method=POST", "method")]
    public void InvalidFiltersAreFieldErrors(string query, string field)
        => Assert.Equal(field, Assert.Throws<ArgumentException>(() => TimingListFilter.Parse(new QueryCollection(QueryHelpers.ParseQuery(query)))).ParamName);

    [Fact]
    public void BusinessFiltersApplyBeforePagingAndCounting()
    {
        var filter = TimingListFilter.Parse(new QueryCollection(QueryHelpers.ParseQuery("?method=get&requestCount=10&avgTime=50&avgTimeOp=%3C")));
        var rows = new[] { (Count: 5L, Time: 20d), (Count: 20L, Time: 30d), (Count: 30L, Time: 100d), (Count: 40L, Time: 10d) };
        var result = ManagementLists.From(rows.Where(row => filter.Matches("GET", "upstream", row.Count, row.Time)))
            .Sort("count", row => row.Count).Read(new QueryCollection(QueryHelpers.ParseQuery("?pageSize=1")), "count");
        Assert.Equal(2, result.Total);
        Assert.Equal(20, Assert.Single(result.Data).Count);
    }
}
