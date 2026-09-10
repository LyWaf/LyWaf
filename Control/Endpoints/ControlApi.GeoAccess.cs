using System.Diagnostics;
using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Captcha;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Config;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Packaging;
using LyWaf.Services.Auth;
using LyWaf.Services.WafRule;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Acme;
using LyWaf.Shared;
using System.Security.Cryptography;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.RuntimeState;
using LyWaf.Services;
using LyWaf.Policy;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record CountryAccessResponse(bool Success, string Message, string Country, DateTime Timestamp);
    private sealed record RegionAccessResponse(bool Success, string Message, string Region, DateTime Timestamp);
    private static void MapGeoAccessEndpoints(WebApplication app, int controlPort)
    {
        // =============== 地理位置黑名单管理 API ===============

        // 获取地理位置黑名单
        app.MapGet("/api/geo/deny-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetDenyCountries();
            return TypedResults.Ok(ManagementLists.From(countries).Sort("value", value => value).Search(value => value).Read(ctx.Request.Query, "value"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加国家到地理位置黑名单
        app.MapPost("/api/geo/deny-countries/add", (HttpContext ctx, IAccessControlService accessControlService, AddCountryRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddDenyCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加禁止访问国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
        }).Produces<CountryAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 从地理位置黑名单移除国家
        app.MapPost("/api/geo/deny-countries/remove", (HttpContext ctx, IAccessControlService accessControlService, RemoveCountryRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveDenyCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从禁止列表移除国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态黑名单" }, statusCode: 404);
                }
        }).Produces<CountryAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取禁止访问的省份列表
        app.MapGet("/api/geo/deny-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetDenyRegions();
            return TypedResults.Ok(ManagementLists.From(regions).Sort("value", value => value).Search(value => value).Read(ctx.Request.Query, "value"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加省份到禁止列表
        app.MapPost("/api/geo/deny-regions/add", (HttpContext ctx, IAccessControlService accessControlService, AddRegionRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddDenyRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加禁止访问省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
        }).Produces<RegionAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 从禁止列表移除省份
        app.MapPost("/api/geo/deny-regions/remove", (HttpContext ctx, IAccessControlService accessControlService, RemoveRegionRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveDenyRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从禁止列表移除省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态黑名单" }, statusCode: 404);
                }
        }).Produces<RegionAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取允许访问的国家列表
        app.MapGet("/api/geo/allow-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetAllowCountries();
            return TypedResults.Ok(ManagementLists.From(countries).Sort("value", value => value).Search(value => value).Read(ctx.Request.Query, "value"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加国家到允许列表
        app.MapPost("/api/geo/allow-countries/add", (HttpContext ctx, IAccessControlService accessControlService, AddCountryRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddAllowCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加允许访问国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
        }).Produces<CountryAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 从允许列表移除国家
        app.MapPost("/api/geo/allow-countries/remove", (HttpContext ctx, IAccessControlService accessControlService, RemoveCountryRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveAllowCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从允许列表移除国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态白名单" }, statusCode: 404);
                }
        }).Produces<CountryAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取允许访问的省份列表
        app.MapGet("/api/geo/allow-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetAllowRegions();
            return TypedResults.Ok(ManagementLists.From(regions).Sort("value", value => value).Search(value => value).Read(ctx.Request.Query, "value"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加省份到允许列表
        app.MapPost("/api/geo/allow-regions/add", (HttpContext ctx, IAccessControlService accessControlService, AddRegionRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddAllowRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加允许访问省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
        }).Produces<RegionAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 从允许列表移除省份
        app.MapPost("/api/geo/allow-regions/remove", (HttpContext ctx, IAccessControlService accessControlService, RemoveRegionRequest request) =>
        {

                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveAllowRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从允许列表移除省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态白名单" }, statusCode: 404);
                }
        }).Produces<RegionAccessResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
