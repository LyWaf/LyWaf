using LyWaf.Control;
using LyWaf.Services.AccessControl;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class IpAccessListTests
{
    [Fact]
    public void MembersRemainInsideTheirGroupAndDirectIpsArePaged()
    {
        var groups = new[] { new IpGroup { Id = "office", Name = "Office", WhitelistEnabled = true, Entries = ["10.0.0.1"] } };
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?page=2&pageSize=1");
        var page = ControlApi.ReadIpAccessPage(groups, ["10.0.0.1", "10.0.0.2"], true, context.Request.Query);
        Assert.Equal(2, page.Total);
        Assert.Equal("office", Assert.Single(page.Data).Group!.Id);
    }

    [Fact]
    public void SearchMatchesGroupMembersAndFiltersBeforeCounting()
    {
        var groups = new[] { new IpGroup { Name = "Office", WhitelistEnabled = true, Entries = ["10.0.0.1"] } };
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?search=10.0.0.1");
        var page = ControlApi.ReadIpAccessPage(groups, ["10.0.0.1", "10.0.0.2"], true, context.Request.Query);
        Assert.Equal(1, page.Total);
        Assert.NotNull(Assert.Single(page.Data).Group);
        Assert.Empty(ControlApi.ReadIpAccessPage(groups, [], false, context.Request.Query).Data);
    }
}
