using LyWaf.Services.AccessControl;
using LyWaf.Services.BlackWhiteList;
using LyWaf.Shared;
using LyWaf.Utils;
using NLog;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;

/// <summary>
/// 访问控制和连接限制中间件
/// 处理黑白名单规则、IP 访问控制、地理位置限制和连接数限制
/// </summary>
public class AccessControlMiddleware(
    RequestDelegate next,
    IAccessControlService accessControlService,
    IBlackWhiteListService bwService)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next = next;
    private readonly IAccessControlService _accessControlService = accessControlService;
    private readonly IBlackWhiteListService _bwService = bwService;

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _accessControlService.GetOptions();
        var clientIp = RequestUtil.GetClientIp(context.Request);
        var path = context.Request.Path.Value ?? "/";

        // 0. 黑白名单规则检查（白名单放行跳过后续检查，黑名单直接拦截）
        var bwResult = _bwService.CheckRequest(context, clientIp);
        if (bwResult.IsMatched)
        {
            var geoInfo = _accessControlService.GetGeoInfo(clientIp);
            var host = context.Request.Host.ToString();
            var application = $"{context.Request.Scheme}://{host}/";

            _bwService.RecordHit(clientIp, application,
                geoInfo?.Region ?? "", geoInfo?.City ?? "",
                bwResult.MatchedRule!.Name, bwResult.MatchedRule!.Type.ToString());

            if (bwResult.RuleType == BwRuleType.Black)
            {
                _logger.Warn("黑名单拦截: ClientIp={ClientIp}, Rule={Rule}, Host={Host}",
                    clientIp, bwResult.MatchedRule!.Name, host);

                SharedData.Security.RecordEvent(SecurityEventType.BlacklistBlock, clientIp);

                await WafUtil.WriteErrorOutput(context, 403,
                    new Dictionary<string, string?>
                    {
                        ["reason"] = $"黑名单规则: {bwResult.MatchedRule!.Name}",
                    },
                    isIntercept: true, isAttack: false,
                    eventType: SecurityEventType.BlacklistBlock);
                return;
            }
            // 白名单命中：跳过后续 IP/地理 访问控制检查，直接进入连接限制
        }
        else
        {
            // 1. 访问控制检查（IP + 地理位置）— 仅在未命中白名单时执行
            var checkResult = _accessControlService.CheckAccess(clientIp, path);
            if (!checkResult.IsAllowed)
            {
                // 记录到检测事件
                var geoInfo = checkResult.GeoInfo ?? _accessControlService.GetGeoInfo(clientIp);
                var host = context.Request.Host.ToString();
                var application = $"{context.Request.Scheme}://{host}/";
                var ruleName = checkResult.DenyReason switch
                {
                    AccessDenyReason.IpDenied => "IP黑名单",
                    AccessDenyReason.PathIpDenied => "路径IP限制",
                    AccessDenyReason.GeoDenied => $"地理位置限制({geoInfo?.Country}/{geoInfo?.Region})",
                    AccessDenyReason.PathGeoDenied => $"路径地理位置限制({geoInfo?.Country}/{geoInfo?.Region})",
                    _ => "访问控制"
                };
                _bwService.RecordHit(clientIp, application,
                    geoInfo?.Region ?? "", geoInfo?.City ?? "",
                    ruleName, "Black");

                await WriteRejectResponse(context, checkResult, clientIp);
                return;
            }
        }

        // 2. 连接限制检查
        if (options.ConnectionLimit.Enabled)
        {
            // 获取目标服务器地址（如果有）
            string? destination = null;
            var endpoint = context.GetEndpoint();
            if (endpoint != null)
            {
                var route = endpoint.Metadata.GetMetadata<RouteModel>();
                destination = route?.Config.ClusterId;
            }

            if (!_accessControlService.TryAcquireConnection(clientIp, destination, path))
            {
                _logger.Warn("连接数超限: ClientIp={ClientIp}, Destination={Destination}, Path={Path}",
                    clientIp, destination, path);

                // 如果 RejectMessage 为空，使用 WafUtil 的模板
                if (string.IsNullOrEmpty(options.ConnectionLimit.RejectMessage))
                {
                    await WafUtil.WriteErrorOutput(context, options.ConnectionLimit.RejectStatusCode,
                        new Dictionary<string, string?> { ["reason"] = "连接数超限" });
                }
                else
                {
                    context.Response.StatusCode = options.ConnectionLimit.RejectStatusCode;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    var message = WafUtil.FormatMessage(options.ConnectionLimit.RejectMessage, context);
                    await context.Response.WriteAsync(message);
                }
                return;
            }

            try
            {
                await _next(context);
            }
            finally
            {
                // 请求完成后释放连接
                _accessControlService.ReleaseConnection(clientIp, destination, path);
            }
        }
        else
        {
            await _next(context);
        }
    }

    /// <summary>
    /// 写入拒绝响应
    /// </summary>
    private async Task WriteRejectResponse(HttpContext context, AccessCheckResult checkResult, string clientIp)
    {
        var geoInfo = checkResult.GeoInfo;

        // 构建拒绝原因描述和安全事件类型
        var (reasonDesc, eventType) = checkResult.DenyReason switch
        {
            AccessDenyReason.IpDenied => ("IP黑名单", SecurityEventType.BlacklistBlock),
            AccessDenyReason.PathIpDenied => ("路径IP限制", SecurityEventType.BlacklistBlock),
            AccessDenyReason.GeoDenied => ($"地理位置限制({geoInfo?.Country}/{geoInfo?.Region})", SecurityEventType.GeoBlock),
            AccessDenyReason.PathGeoDenied => ($"路径地理位置限制({geoInfo?.Country}/{geoInfo?.Region})", SecurityEventType.GeoBlock),
            _ => ("访问被拒绝", SecurityEventType.BlacklistBlock)
        };

        switch (checkResult.DenyReason)
        {
            case AccessDenyReason.IpDenied:
            case AccessDenyReason.PathIpDenied:
                _logger.Warn("IP 访问被拒绝: {ClientIp}, Reason: {Reason}", clientIp, checkResult.DenyReason);
                break;
            case AccessDenyReason.GeoDenied:
            case AccessDenyReason.PathGeoDenied:
                _logger.Warn("地理位置访问被拒绝: {ClientIp}, Country: {Country}, Region: {Region}, City: {City}, Reason: {Reason}",
                    clientIp, geoInfo?.Country, geoInfo?.Region, geoInfo?.City, checkResult.DenyReason);
                break;
        }

        var extraValues = new Dictionary<string, string?>
        {
            ["reason"] = reasonDesc,
            ["Country"] = geoInfo?.Country ?? "Unknown",
            ["Region"] = geoInfo?.Region ?? "",
            ["City"] = geoInfo?.City ?? "",
            ["Isp"] = geoInfo?.Isp ?? ""
        };

        // 如果 RejectMessage 为空，使用 WafUtil 的模板
        if (string.IsNullOrEmpty(checkResult.RejectMessage))
        {
            await WafUtil.WriteErrorOutput(context, checkResult.RejectStatusCode, extraValues,
                isIntercept: true, isAttack: false, eventType: eventType);
            return;
        }

        // 记录安全事件
        SharedData.Security.RecordEvent(eventType, clientIp);

        // 使用自定义消息
        context.Response.StatusCode = checkResult.RejectStatusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";

        var message = WafUtil.FormatMessage(checkResult.RejectMessage, context, extraValues);
        await context.Response.WriteAsync(message);
    }
}
