using LyWaf.Control;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class HistoryQueryTests
{
    [Theory]
    [InlineData("?from=invalid", "from")]
    [InlineData("?from=2026-01-02&to=2026-01-01", "to")]
    [InlineData("?from=2020-01-01&to=2026-01-01", "from")]
    [InlineData("?from=2026-01-01&from=2026-02-01", "from")]
    public void InvalidTimesCannotSilentlyUseDefaults(string query, string field)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        Assert.Equal(field, Assert.Throws<ArgumentException>(() => HistoryQuery.Range(context.Request.Query, TimeSpan.FromDays(1))).ParamName);
    }

    [Fact]
    public void ExplicitOffsetsAreNormalizedToUtc()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?from=2026-09-09T08:00:00%2B08:00&to=2026-09-09T09:00:00%2B08:00");
        var range = HistoryQuery.Range(context.Request.Query, TimeSpan.FromDays(1));
        Assert.Equal(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), range.From);
        Assert.Equal(TimeSpan.FromHours(1), range.To - range.From);
    }
}
