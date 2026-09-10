using LyWaf.Services.AccessControl;
using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Control;

public static partial class ControlApi
{
    internal sealed record IpAccessListItem(string Id, string Name, string? Ip, IpGroup? Group, DateTime? UpdatedAt);

    internal static ManagementPage<IpAccessListItem> ReadIpAccessPage(IEnumerable<IpGroup> groups,
        IEnumerable<string> ips, bool whitelist, IQueryCollection query)
    {
        var selected = groups.Where(group => whitelist ? group.WhitelistEnabled : group.BlacklistEnabled).ToArray();
        var members = selected.SelectMany(group => group.Entries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = selected.Select(group => new IpAccessListItem("group:" + group.Id, group.Name, null, group, group.UpdatedAt))
            .Concat(ips.Where(ip => !members.Contains(ip)).Select(ip => new IpAccessListItem("ip:" + ip, ip, ip, null, null)));
        return ManagementLists.From(items).Sort("name", item => item.Name)
            .Sort("updatedAt", item => item.UpdatedAt).Timestamp(item => item.UpdatedAt)
            .Search(item => item.Name + " " + item.Group?.Description + " " + string.Join(' ', item.Group?.Entries ?? []))
            .Read(query, "name");
    }

    private static void MapIpAccessListEndpoints(WebApplication app, int controlPort)
    {
        app.MapGet("/api/ac/{list}/entries", (string list, HttpContext ctx,
            IAccessControlService access, IIpGroupService groups) =>
        {
            if (list is not ("whitelist" or "blacklist"))
                return ManagementApiContract.Error(ctx, 404, "not_found", "名单不存在");
            var whitelist = list == "whitelist";
            var ips = whitelist ? access.GetWhitelist() : access.GetBlacklist();
            return Results.Ok(ReadIpAccessPage(groups.GetAll(), ips, whitelist, ctx.Request.Query));
        }).Produces<ManagementPage<IpAccessListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();
    }
}
