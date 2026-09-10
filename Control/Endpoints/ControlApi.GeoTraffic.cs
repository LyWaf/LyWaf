using System.Diagnostics;
using System.Text;
using LyWaf.Control.Endpoints;
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
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.RuntimeState;
using LyWaf.Services;
using LyWaf.Policy;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapGeoTrafficEndpoints(WebApplication app, int controlPort)
    {
        // =============== 地理位置流量统计 API ===============

        app.MapGet("/api/geo-traffic/stats", (HttpContext ctx) =>
        {
                var snapshot = SharedData.GeoTraffic.GetSnapshot();
                return TypedResults.Ok(new
                {
                    success = true,
                    countryVisits = snapshot.CountryVisits
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    countryIntercepts = snapshot.CountryIntercepts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    regionVisits = snapshot.RegionVisits
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    regionIntercepts = snapshot.RegionIntercepts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}

