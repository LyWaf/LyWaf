using System.Reflection;
using System.Text.Json;
using LyWaf.Control;
using LyWaf.Control.Endpoints;
using LyWaf.Services.Statistic;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Acme;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Backup;
using LyWaf.Services.Incidents;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.ManagementTasks;
using LyWaf.Services.Notifications;
using LyWaf.Services.Sites;
using LyWaf.Services.WafRule;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Protect;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.ResponseCaching;
using LyWaf.Services.Captcha;
using LyWaf.Services.ABTest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yarp.ReverseProxy;

namespace LyWaf.Tests;

public sealed class ManagementBusinessContractTests
{
    [Fact]
    public async Task RegisteredBusinessEndpointsDeclareTheirActualSuccessModels()
    {
        var builder = WebApplication.CreateBuilder();
        Type[] services = [typeof(ManagedSiteProvider), typeof(SiteStatisticsService), typeof(ManagementJobService),
            typeof(IAcmeService), typeof(IProxyStateLookup), typeof(IAuditLogService), typeof(SecurityIncidentService),
            typeof(IWafRuleService), typeof(IAccessControlService), typeof(IncidentRequestStore), typeof(NotificationService),
            typeof(ConfigurationBackupService), typeof(ISpeedLimitService), typeof(IProtectService), typeof(IIpGroupService),
            typeof(IRuntimeStateStore), typeof(ICaptchaService), typeof(IABTestService), typeof(IStatisticService),
            typeof(LyWaf.Services.DuckDb.RuntimeLogService), typeof(LyWaf.Services.DuckDb.IDuckDbQueryService),
            typeof(LyWaf.Plugins.Core.IPluginManager), typeof(LyWaf.Plugins.Packaging.PluginPackageManager),
            typeof(LyWaf.Services.Lifecycle.UpgradePackageService), typeof(LyWaf.Services.Lifecycle.GracefulShutdownCoordinator),
            typeof(ResponseCacheStore), typeof(RuntimeStateAuthentication), typeof(LyWaf.Policy.OutlierDetectionRegistry)];
        foreach (var type in services)
            builder.Services.AddSingleton(type, _ => throw new InvalidOperationException("Contract generation must not activate business services"));
        await using var app = builder.Build();
        foreach (var method in new[] { "MapSiteEndpoints", "MapSecurityIncidentEndpoints", "MapBackupEndpoints", "MapNotificationEndpoints", "MapGeoAccessEndpoints", "MapThrottlingEndpoints", "MapConnectionLimitEndpoints", "MapAccessControlEndpoints", "MapIpLogEndpoints", "MapBlockedIpEndpoints", "MapAbTestEndpoints", "MapParameterEndpoints", "MapLegacyWafEndpoints" })
            typeof(ControlApi).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, 12345]);
        typeof(ControlApi).GetMethod("MapCcProtectionEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, 12345]);
        app.MapWafRuleEndpoints(12345);
        typeof(ControlApi).GetMethod("MapDestinationListEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, 12345]);
        typeof(ControlApi).GetMethod("MapSettingsEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [app, 12345, new LyWaf.Services.WafInfo.WafInfoOptions()]);
        foreach (var method in new[] { "MapDashboardEndpoints", "MapHistoryEndpoints", "MapGeoTrafficEndpoints", "MapPluginEndpoints", "MapErrorTemplateEndpoints", "MapInterceptEndpoints" })
            typeof(ControlApi).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, 12345]);
        foreach (var method in new[] { "MapOverviewEndpoints", "MapConfigFileEndpoints", "MapListenerEndpoints" })
            typeof(ControlApi).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [app, 12345, new LyWaf.Services.WafInfo.WafInfoOptions()]);
        app.MapLyWafResponseCacheApi(12345);
        app.MapLyWafRuntimeStateApi(12345);
        typeof(ControlApi).GetMethod("MapUpstreamHealthEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app, 12345]);
        using var source = new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var isBackup = endpoint.RoutePattern.RawText!.StartsWith("/api/backups", StringComparison.Ordinal);
            var isHealthUpdate = endpoint.RoutePattern.RawText!.TrimEnd('/') == "/api/lb/clusters/{clusterId}/health-check" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT");
            Assert.Equal(isBackup || isHealthUpdate, endpoint.Metadata.GetMetadata<LyWaf.Services.Auth.ManagementStepUpExemption>() != null);
            if (isBackup || isHealthUpdate)
                Assert.Contains(endpoint.Metadata.GetOrderedMetadata<LyWaf.Services.Auth.ManagementPermissionRequirement>(),
                    permission => permission.Permission == LyWaf.Services.Auth.ManagementPermissions.ManageSystem);
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ManagementApiContract.BuildOpenApiDocument(source)));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in paths.EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!path.Name.Contains("/agent/") && path.Name != "/api/v1/health/ready")
                    Assert.NotEmpty(operation.Value.GetProperty("x-permissions").EnumerateArray());
                foreach (var status in operation.Value.GetProperty("responses").EnumerateObject().Where(s => s.Name.StartsWith('2')))
                    Assert.True(status.Value.TryGetProperty("content", out _), $"{operation.Name} {path.Name} {status.Name}");
            }
        foreach (var path in new[] { "/api/v1/sites", "/api/v1/backups", "/api/v1/security-incidents", "/api/v1/waf-rules" })
            Assert.Equal(7, paths.GetProperty(path).GetProperty("get").GetProperty("parameters").GetArrayLength());
        var download = paths.GetProperty("/api/v1/backups/{id}/download").GetProperty("get").GetProperty("responses");
        Assert.Equal("binary", download.GetProperty("200").GetProperty("content").GetProperty("application/octet-stream")
            .GetProperty("schema").GetProperty("format").GetString());
        Assert.True(download.TryGetProperty("206", out _));
        Assert.True(paths.GetProperty("/api/v1/sites").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(paths.GetProperty("/api/v1/runtime-state/agent/merge").GetProperty("post").GetProperty("security")[0]
            .TryGetProperty("runtimeStateSignature", out _));
    }
}
