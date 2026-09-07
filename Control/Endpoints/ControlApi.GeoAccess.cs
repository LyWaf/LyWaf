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
    private static void MapGeoAccessEndpoints(WebApplication app, int controlPort)
    {
        // =============== 地理位置黑名单管理 API ===============
        
        // 获取地理位置黑名单
        app.MapGet("/api/geo/deny-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetDenyCountries();
            return Results.Json(new
            {
                success = true,
                count = countries.Count,
                denyCountries = countries,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加国家到地理位置黑名单
        app.MapPost("/api/geo/deny-countries/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCountryRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从地理位置黑名单移除国家
        app.MapPost("/api/geo/deny-countries/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCountryRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取禁止访问的省份列表
        app.MapGet("/api/geo/deny-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetDenyRegions();
            return Results.Json(new
            {
                success = true,
                count = regions.Count,
                denyRegions = regions,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加省份到禁止列表
        app.MapPost("/api/geo/deny-regions/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRegionRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从禁止列表移除省份
        app.MapPost("/api/geo/deny-regions/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRegionRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取允许访问的国家列表
        app.MapGet("/api/geo/allow-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetAllowCountries();
            return Results.Json(new
            {
                success = true,
                count = countries.Count,
                allowCountries = countries,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加国家到允许列表
        app.MapPost("/api/geo/allow-countries/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCountryRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从允许列表移除国家
        app.MapPost("/api/geo/allow-countries/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCountryRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取允许访问的省份列表
        app.MapGet("/api/geo/allow-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetAllowRegions();
            return Results.Json(new
            {
                success = true,
                count = regions.Count,
                allowRegions = regions,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加省份到允许列表
        app.MapPost("/api/geo/allow-regions/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRegionRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从允许列表移除省份
        app.MapPost("/api/geo/allow-regions/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRegionRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}

