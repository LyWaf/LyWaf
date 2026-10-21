using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Services.Statistic;

internal static class CcRuleStateKey
{
    internal static string Revision(AdvancedCcRule rule) => ExtendRevision(rule, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        rule.Id, rule.Type, rule.Period, rule.Threshold, rule.Action, rule.ActionSeconds,
        Conditions = rule.ConditionGroups is null ? rule.Conditions : null,
        GroupOperator = rule.ConditionGroups is null ? null : rule.GroupOperator, rule.ConditionGroups
    }))));
    private static string ExtendRevision(AdvancedCcRule rule, string legacy)
        => !rule.UseManagedMembership ? ExplicitRevision(rule, legacy)
            : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Revision = ExplicitRevision(rule, legacy), Membership = "managed-controller-v1" })));
    private static string ExplicitRevision(AdvancedCcRule rule, string legacy)
        => rule.Scope == CcScope.SiteGroup && rule.Subject == CcSubjectKind.Ip && !rule.RequireCompleteCoverage && (rule.ExpectedNodes?.Length ?? 0) == 0
            ? legacy : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { legacy, rule.Scope, rule.Subject, rule.SubjectAuthenticationType, rule.SubjectIssuer,
                rule.RequireCompleteCoverage, ExpectedNodes = (rule.ExpectedNodes ?? []).Order(StringComparer.Ordinal).ToArray() })));

    internal static string ResolveScope(AdvancedCcRule rule, string siteScope, string nodeId)
        => rule.Scope switch
        {
            CcScope.Node => "node:" + nodeId,
            CcScope.Cluster => "cluster:shared",
            _ => !string.IsNullOrWhiteSpace(rule.ProtectionGroup) ? "group:" + rule.ProtectionGroup.Trim() : siteScope
        };
    private static readonly object ScopeItem = new();

    public static string Scope(HttpContext context)
    {
        if (context.Items.TryGetValue(ScopeItem, out var cached)) return (string)cached!;
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId;
        var scope = !string.IsNullOrEmpty(route) ? "route:" + route
            : "host:" + context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
        context.Items[ScopeItem] = scope;
        return scope;
    }

    public static string Create(AdvancedCcRule rule, string clientIp, string scope = "legacy-global")
    {
        if (rule.Scope == CcScope.Cluster) scope = "cluster:shared";
        else if (rule.Scope == CcScope.SiteGroup && !string.IsNullOrWhiteSpace(rule.ProtectionGroup)) scope = "group:" + rule.ProtectionGroup.Trim();
        if (rule.Scope == CcScope.Node && !scope.StartsWith("node:", StringComparison.Ordinal))
            throw new ArgumentException("Node-scoped CC state requires an explicit node identity");
        // Exclude presentation metadata so identical policies share counters across nodes.
        var policy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            scope, rule.Id, rule.Type, rule.Period, rule.Threshold, rule.Action, rule.ActionSeconds,
            Conditions = rule.ConditionGroups is null ? rule.Conditions : null,
            GroupOperator = rule.ConditionGroups is null ? null : rule.GroupOperator,
            rule.ConditionGroups
        });
        return $"v2:{ExtendRevision(rule, Convert.ToHexString(SHA256.HashData(policy)))}:{clientIp}";
    }

    public static string Current(IRuntimeStateStore store, AdvancedCcRule rule, string clientIp, HttpContext context)
    {
        if (!CcRequestIdentity.TrySubject(context, rule, clientIp, out var subject)) return "";
        var key = Create(rule, subject, ResolveScope(rule, Scope(context), store.GetStatus().NodeId));
        return store.GetCcPolicyGeneration(key).StateKey(key);
    }
}
