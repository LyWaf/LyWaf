using LyWaf.Middleware;
using LyWaf.Plugins.Core;
using LyWaf.Services.WebTamper;
using LyWaf.Services.AuditLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Sys;

/// <summary>系统插件：按网站和路径维护响应基线，阻止网页内容被篡改。</summary>
public sealed class WebTamperPlugin : LyWafPluginBase, IPluginApiHandler
{
    public override PluginMetadata Metadata => new()
    {
        Id = "web-tamper",
        Name = "网页防篡改",
        Version = "1.0.0",
        Description = "按网站与路径维护安全基线，检测变更并向访客返回已确认内容",
        Author = "LyWaf Team",
        Priority = PluginPriority.Highest,
        EnabledByDefault = true
    };

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IWebTamperService, WebTamperService>();
        services.AddHostedService<WebTamperMonitorService>();
    }

    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        proxyApp.UseMiddleware<WebTamperMiddleware>();
    }

    public override IReadOnlyList<PluginAction> GetActions() =>
    [
        new PluginAction
        {
            Id = "manage",
            Name = "管理网页防篡改",
            Type = "page-link",
            Description = "管理防护网站、资源基线和变更事件",
            Path = "/plugins/web-tamper"
        }
    ];

    public async Task<IResult> HandleApiAsync(string path, HttpContext context)
    {
        path = path.Trim('/');
        var method = context.Request.Method;
        var service = context.RequestServices.GetRequiredService<IWebTamperService>();

        if (method == HttpMethods.Get && path == "websites") return GetWebsites(service, context.RequestServices.GetRequiredService<IConfiguration>());
        if (method == HttpMethods.Get && path == "resources") return Results.Json(new { success = true, resources = service.GetResources() });
        if (method == HttpMethods.Get && path == "events") return Results.Json(new { success = true, events = service.GetEvents() });

        if (method == HttpMethods.Post && path == "websites") return await AddWebsiteAsync(service, context);
        if (method == HttpMethods.Put && path == "websites/name") return await RenameWebsiteAsync(service, context);
        if (method == HttpMethods.Delete && path.StartsWith("websites/", StringComparison.Ordinal))
            return DeleteWebsite(service, Uri.UnescapeDataString(path["websites/".Length..]), context);

        if (method == HttpMethods.Post && path == "resources") return await AddResourceAsync(service, context);
        if (path.StartsWith("resources/", StringComparison.Ordinal))
        {
            var remaining = path["resources/".Length..];
            var slash = remaining.IndexOf('/');
            var id = Uri.UnescapeDataString(slash < 0 ? remaining : remaining[..slash]);
            var action = slash < 0 ? "" : remaining[(slash + 1)..];
            if (method == HttpMethods.Put && action.Length == 0) return await UpdateResourceAsync(service, id, context);
            if (method == HttpMethods.Delete && action.Length == 0) return DeleteResource(service, id, context);
            if (method == HttpMethods.Post && action == "check") return await CheckResourceAsync(service, id, context);
            if (method == HttpMethods.Post && action == "accept") return await AcceptResourceAsync(service, id, context);
        }

        return Results.NotFound(new { success = false, message = $"网页防篡改插件 API 不存在: {method} {path}" });
    }

    private static IResult GetWebsites(IWebTamperService service, IConfiguration configuration)
    {
        var sites = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in configuration.GetSection("ReverseProxy:Routes").GetChildren())
        foreach (var hostItem in route.GetSection("Match:Hosts").GetChildren())
        {
            var pattern = hostItem.Value?.Trim();
            if (string.IsNullOrEmpty(pattern) || pattern.Contains('*') || pattern.Contains('{')) continue;
            var host = pattern;
            int? port = null;
            if (pattern.StartsWith('['))
            {
                var bracket = pattern.IndexOf(']');
                if (bracket > 0)
                {
                    host = pattern[..(bracket + 1)];
                    if (bracket + 2 < pattern.Length && int.TryParse(pattern[(bracket + 2)..], out var parsed)) port = parsed;
                }
            }
            else
            {
                var colon = pattern.LastIndexOf(':');
                if (colon > 0 && int.TryParse(pattern[(colon + 1)..], out var parsed)) { host = pattern[..colon]; port = parsed; }
            }
            host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            var authorityHost = host.Contains(':') ? $"[{host}]" : host;
            var authority = port.HasValue ? $"{authorityHost}:{port.Value}" : authorityHost;
            sites[authority] = new { id = $"config:{authority}", name = authority, host = authority, domain = authority, source = "config", removable = false };
        }
        foreach (var website in service.GetWebsites())
        {
            var manual = website.Source != "config";
            sites[website.Origin] = new { id = manual ? website.Id : $"config:{website.Origin}", name = website.Name, host = website.Origin, domain = website.Origin, source = manual ? "manual" : "config", removable = manual };
        }
        return Results.Json(new { success = true, websites = sites.Values });
    }

    private static async Task<IResult> AddWebsiteAsync(IWebTamperService service, HttpContext context)
    {
        try
        {
            var request = await context.Request.ReadFromJsonAsync<CreateWebsiteRequest>();
            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            var website = service.AddWebsite(request.Name, request.Origin, request.Port);
            Audit(context, $"添加防篡改网站: {website.Name} {website.Origin}");
            return Results.Json(new { success = true, website, message = "网站已添加" });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
    }

    private static async Task<IResult> RenameWebsiteAsync(IWebTamperService service, HttpContext context)
    {
        try
        {
            var request = await context.Request.ReadFromJsonAsync<RenameWebsiteRequest>();
            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            var website = service.RenameWebsite(request.Name, request.Origin, request.Source);
            Audit(context, $"修改防篡改网站备注: {website.Name} {website.Origin}");
            return Results.Json(new { success = true, website, message = "网站备注已更新" });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
    }

    private static IResult DeleteWebsite(IWebTamperService service, string id, HttpContext context)
    {
        try
        {
            if (!service.DeleteWebsite(id)) return Results.NotFound(new { success = false, message = "网站不存在" });
            Audit(context, $"删除防篡改网站: {id}");
            return Results.Json(new { success = true, message = "网站已删除" });
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
    }

    private static async Task<IResult> AddResourceAsync(IWebTamperService service, HttpContext context)
    {
        try
        {
            var request = await context.Request.ReadFromJsonAsync<CreateResourceRequest>();
            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            var resource = await service.AddAsync(request.Website, request.Path, context.RequestAborted);
            Audit(context, $"添加网页防篡改资源: {resource.Name}");
            return Results.Json(new { success = true, resource, message = resource.IsPattern ? "通配保护规则已创建，匹配路径首次访问时将建立独立基线" : "资源基线已创建" });
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException)
        { return Results.BadRequest(new { success = false, message = $"抓取资源失败\n{ex.Message}" }); }
    }

    private static async Task<IResult> UpdateResourceAsync(IWebTamperService service, string id, HttpContext context)
    {
        try
        {
            var request = await context.Request.ReadFromJsonAsync<UpdateResourceRequest>();
            if (request == null) return Results.BadRequest(new { success = false, message = "请求内容不能为空" });
            return service.Update(id, request.Name, request.Enabled) ? Results.Json(new { success = true, message = "资源已更新" }) : Results.NotFound(new { success = false, message = "资源不存在" });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
    }

    private static IResult DeleteResource(IWebTamperService service, string id, HttpContext context)
    {
        if (!service.Delete(id)) return Results.NotFound(new { success = false, message = "资源不存在" });
        Audit(context, $"删除网页防篡改资源: {id}");
        return Results.Json(new { success = true, message = "资源已删除" });
    }

    private static async Task<IResult> CheckResourceAsync(IWebTamperService service, string id, HttpContext context)
    {
        try
        {
            var comparison = await service.CheckAsync(id, context.RequestAborted);
            return comparison == null ? Results.NotFound(new { success = false, message = "资源不存在" }) : Results.Json(new { success = true, comparison });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        { return Results.BadRequest(new { success = false, message = $"检查失败: {ex.Message}" }); }
    }

    private static async Task<IResult> AcceptResourceAsync(IWebTamperService service, string id, HttpContext context)
    {
        try
        {
            var comparison = await service.AcceptCurrentAsync(id, context.RequestAborted);
            if (comparison == null) return Results.NotFound(new { success = false, message = "资源不存在" });
            Audit(context, $"更新网页防篡改基线: {comparison.Resource.Name}");
            return Results.Json(new { success = true, comparison, message = "安全基线已更新" });
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException)
        { return Results.BadRequest(new { success = false, message = $"更新基线失败: {ex.Message}" }); }
    }

    private static void Audit(HttpContext context, string action) =>
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown", action,
            context.Connection.RemoteIpAddress?.ToString() ?? "");

    private sealed class CreateResourceRequest { public string Website { get; set; } = ""; public string Path { get; set; } = "/"; }
    private sealed class CreateWebsiteRequest { public string Name { get; set; } = ""; public string Origin { get; set; } = ""; public int? Port { get; set; } }
    private sealed class RenameWebsiteRequest { public string Name { get; set; } = ""; public string Origin { get; set; } = ""; public string Source { get; set; } = "manual"; }
    private sealed class UpdateResourceRequest { public string Name { get; set; } = ""; public bool Enabled { get; set; } }
}
