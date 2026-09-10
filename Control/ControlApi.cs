using LyWaf.Control.Endpoints;
using LyWaf.Services.WafInfo;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Auth;

namespace LyWaf.Control;

public static partial class ControlApi
{
    /// <summary>
    /// 注册控制台 API 路由
    /// </summary>
    public static WebApplication MapControlApi(this WebApplication app, WafInfoOptions wafInfos)
    {
        var controlListen = wafInfos.GetControlListen();
        var controlPort = controlListen.Port;

        app.UseManagementApiContract(controlPort);
        // 显式固定顺序：版本路径重写必须发生在端点路由匹配之前。
        app.UseRouting();
        
        MapFrontendEndpoints(app, controlPort);
        MapAuthenticationEndpoints(app, controlPort);
        app.UseManagementStepUp(controlPort);
        app.UseManagementIdempotency(controlPort);
        MapManagementIdentityEndpoints(app, controlPort);
        MapManagementRoleEndpoints(app, controlPort);
        MapManagementMfaEndpoints(app, controlPort);
        MapManagementTaskEndpoints(app, controlPort);
        MapBackupEndpoints(app, controlPort);
        MapSiteEndpoints(app, controlPort);
        MapSecurityIncidentEndpoints(app, controlPort);
        MapNotificationEndpoints(app, controlPort);
        // 业务端点必须在认证管线之后注册。
        MapErrorTemplateEndpoints(app, controlPort);
        app.MapWafRuleEndpoints(controlPort);
        MapInterceptEndpoints(app, controlPort);
        MapGeoTrafficEndpoints(app, controlPort);
        MapDashboardEndpoints(app, controlPort);
        MapOverviewEndpoints(app, controlPort, wafInfos);
        MapConfigFileEndpoints(app, controlPort, wafInfos);
        MapConnectionLimitEndpoints(app, controlPort);
        MapAccessControlEndpoints(app, controlPort);
        MapBlockedIpEndpoints(app, controlPort);
        MapIpLogEndpoints(app, controlPort);
        MapLegacyWafEndpoints(app, controlPort);
        MapCcProtectionEndpoints(app, controlPort);
        MapGeoAccessEndpoints(app, controlPort);
        MapThrottlingEndpoints(app, controlPort);
        MapAbTestEndpoints(app, controlPort);
        MapLoadBalancingEndpoints(app, controlPort);
        MapRouteEndpoints(app, controlPort);
        MapFileServerEndpoints(app, controlPort);
        MapSimpleResponseEndpoints(app, controlPort);
        MapPatchEndpoints(app, controlPort);
        MapListenerEndpoints(app, controlPort, wafInfos);
        MapPluginEndpoints(app, controlPort);
        MapSettingsEndpoints(app, controlPort, wafInfos);
        MapHistoryEndpoints(app, controlPort);
        MapParameterEndpoints(app, controlPort);
        MapPcapEndpoints(app, controlPort);
        return app;
    }
}
