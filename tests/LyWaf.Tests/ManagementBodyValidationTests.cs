using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Xunit;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using LyWaf.Control;
using LyWaf.Services.WafRule;

namespace LyWaf.Tests;

public sealed class ManagementBodyValidationTests
{
    private sealed record Child(string Name);
    private sealed record Request(string Name, Child[] Children, string? Note = null);

    private sealed record Renamed([property: Required, JsonPropertyName("display_name")] string Name);
    private sealed record DictionaryRequest(Dictionary<string, string> Required, Dictionary<string, string?> Optional);

    [Fact]
    public void DictionaryNullabilityIsValidatedWithoutLeakingKeys()
    {
        var fields = Validate(new DictionaryRequest(new() { ["secret-key"] = null! }, new() { ["optional"] = null }));
        Assert.Contains("required[entry]", fields.Keys);
        Assert.DoesNotContain(fields.Keys, field => field.Contains("secret-key") || field.StartsWith("optional"));
        Assert.Empty(Validate(new Dictionary<string, object?> { ["reset"] = null }));
    }

    [Fact]
    public void ValidationUsesWireFieldName()
    {
        var fields = Validate(new Renamed(""));
        Assert.Contains("display_name", fields.Keys);
        Assert.DoesNotContain("name", fields.Keys);
    }

    [Fact]
    public void IpMonitoringRejectsInvalidAddressAndNonPositiveDuration()
    {
        var fields = Validate(new IpLogRequest { Ip = "not-an-ip", Duration = TimeSpan.Zero });
        Assert.Contains("ip", fields.Keys);
        Assert.Contains("duration", fields.Keys);
        Assert.Empty(Validate(new IpLogRequest { Ip = "::1", Duration = TimeSpan.FromMinutes(10) }));
    }

    [Fact]
    public void IpGroupNameCannotBeEmptyOrExceedServiceLimit()
    {
        Assert.Contains("name", Validate(new SaveIpGroupRequest()).Keys);
        Assert.Contains("name", Validate(new SaveIpGroupRequest { Name = new string('a', 65) }).Keys);
    }

    [Fact]
    public void BlockedIpLimitsRejectOverflowBeforeChangingRuntimeState()
    {
        Assert.Contains("speedLimit", Validate(new BlockIpRequest { Ip = "127.0.0.1", SpeedLimit = int.MaxValue }).Keys);
        Assert.Contains("duration", Validate(new BlockIpRequest { Ip = "127.0.0.1", Duration = TimeSpan.MaxValue }).Keys);
        Assert.Contains("type", Validate(new BlockIpRequest { Ip = "127.0.0.1", Type = "unknown" }).Keys);
        Assert.Empty(Validate(new BlockIpRequest { Ip = "::1", Type = "throttled", SpeedLimit = 100, Duration = TimeSpan.FromMinutes(10) }));
    }

    [Fact]
    public void AbWeightsRejectOverflowAndNegativeValues()
    {
        Assert.Contains("variants", Validate(new CreateABTestRequest { TestId = "test", Variants = new() { ["a"] = int.MaxValue, ["b"] = 1 } }).Keys);
        Assert.Contains("variants", Validate(new CreateABTestRequest { TestId = "test", Variants = new() { ["a"] = -1, ["b"] = 10 } }).Keys);
        Assert.Empty(Validate(new CreateABTestRequest { TestId = "test", Variants = new() { ["a"] = 70, ["b"] = 30 } }));
    }

    private static Dictionary<string, string[]> Validate<T>(T body)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AcceptsMetadata(["application/json"], typeof(T))), "test"));
        return ManagementBodyValidation.Validate(new DefaultEndpointFilterInvocationContext(context, body));
    }

    [Fact]
    public void IpGroupsAndRoutePatternsReturnExactFieldPaths()
    {
        Assert.Contains("ipOrCidr", Validate(new AddIpRequest { IpOrCidr = "10.0.0.1/99" }).Keys);
        Assert.Empty(Validate(new AddIpRequest { IpOrCidr = "2001:db8::/64" }));
        Assert.Contains("entries[1]", Validate(new SaveIpGroupRequest { Name = "test", Entries = ["127.0.0.1", "bad"] }).Keys);
        Assert.Contains("path", Validate(new UpdateRouteMatchRequest { Path = "/{broken" }).Keys);
        Assert.Contains("methods[0]", Validate(new UpdateRouteMatchRequest { Methods = ["GET POST"] }).Keys);
        Assert.Empty(Validate(new UpdateRouteMatchRequest { Path = "/{**path}", Methods = ["GET", "POST"] }));
    }

    [Fact]
    public void BandwidthLimitsRejectByteConversionOverflow()
    {
        Assert.Contains("limitKbps", Validate(new SetGlobalThrottleRequest { LimitKbps = int.MaxValue }).Keys);
        Assert.Contains("limitKbps", Validate(new AddIpThrottleRequest { Ip = "127.0.0.1", LimitKbps = int.MaxValue }).Keys);
        Assert.Contains("limitKbps", Validate(new AddPathThrottleRequest { Path = "/", LimitKbps = int.MaxValue }).Keys);
        Assert.Empty(Validate(new SetGlobalThrottleRequest { LimitKbps = int.MaxValue / 1024 }));
        Assert.Empty(Validate(new SetGlobalThrottleRequest { LimitKbps = 0 }));
    }

    [Fact]
    public void SecurityPoliciesReportFieldLevelErrors()
    {
        Assert.Contains("challengeTokenSeconds", Validate(new LyWaf.Control.Endpoints.AntiBotUpdateRequest { ChallengeTokenSeconds = 0 }).Keys);
        Assert.Contains("cookieName", Validate(new LyWaf.Control.Endpoints.AntiBotUpdateRequest { CookieName = "bad;name" }).Keys);
        Assert.Contains("inspectionDecodeDepth", Validate(new LyWaf.Control.Endpoints.RequestSecurityUpdateRequest { InspectionDecodeDepth = 6 }).Keys);
        Assert.Contains("canaryPercentage", Validate(new LyWaf.Control.Endpoints.RulePackageActivateRequest { CanaryPercentage = 101 }).Keys);
        var fields = Validate(new LyWaf.Services.ResponseWaf.ResponseWafOptions { Rules = [new() { Field = "Header", Value = "[" }] });
        Assert.Contains("rules[0].headerName", fields.Keys);
        Assert.Contains("rules[0].value", fields.Keys);
        Assert.Empty(Validate(new LyWaf.Services.ResponseWaf.ResponseWafOptions()));
    }

    [Theory]
    [InlineData("Bad Header", "value")]
    [InlineData("X-Test", "value\r\nInjected: yes")]
    [InlineData("X-Test", "value\0")]
    public void ResponseHeadersRejectInvalidNamesAndControlCharacters(string name, string value)
    {
        Assert.Contains("headers", Validate(new AddSimpleResRequest { ItemId = "test", Headers = new() { [name] = value } }).Keys);
        Assert.Contains("headers", Validate(new UpdateSimpleResRequest { ItemId = "test", Headers = new() { [name] = value } }).Keys);
    }

    [Fact]
    public void ResponseHeadersAndIpLogDurationAreBounded()
    {
        Assert.Empty(Validate(new AddSimpleResRequest { ItemId = "test", Headers = new() { ["X-Test"] = "valid value" } }));
        Assert.Contains("headers", Validate(new AddSimpleResRequest { ItemId = "test", Headers = new() { ["X-Test"] = new string('a', 8193) } }).Keys);
        Assert.Contains("duration", Validate(new IpLogRequest { Ip = "127.0.0.1", Duration = TimeSpan.MaxValue }).Keys);
    }

    [Fact]
    public void ListenerAndDeploymentRequestsRejectInvalidBounds()
    {
        Assert.Contains("port", Validate(new AddListenerRequest { Port = 65536 }).Keys);
        Assert.Contains("host", Validate(new AddListenerRequest { Port = 8080, Host = "http://localhost" }).Keys);
        Assert.Empty(Validate(new AddListenerRequest { Port = 8080, Host = "::1" }));
        Assert.Contains("batchSize", Validate(new LyWaf.Services.Distribution.CreateDistributionRequest("version", BatchSize: 0)).Keys);
    }

    [Theory]
    [InlineData("../outside.pem")]
    [InlineData("..\\outside.pem")]
    [InlineData("C:\\outside.pem")]
    [InlineData("host.pem:stream")]
    [InlineData("host.key")]
    public void CertificateDeleteRejectsUntrustedPaths(string name)
        => Assert.Contains("pemFile", Validate(new CertDeleteRequest { PemFile = name }).Keys);

    [Fact]
    public void CertificateUploadRejectsTraversalAndAllowsWildcardDomains()
    {
        Assert.Contains("domain", Validate(new CertUploadRequest { Domain = "../../outside", PemContent = "pem", KeyContent = "key" }).Keys);
        Assert.Empty(Validate(new CertUploadRequest { Domain = "*.example.com", PemContent = "pem", KeyContent = "key" }));
        Assert.Empty(Validate(new CertDeleteRequest { PemFile = "_wildcard_.example.com.pem" }));
    }

    [Fact]
    public void CcRequestsRejectInvalidEnumsAndCounterPeriods()
    {
        Assert.Contains("period", Validate(new AddCcRuleRequest { Path = "/", Period = 0 }).Keys);
        Assert.Contains("fbTime", Validate(new AddCcRuleRequest { Path = "/", FbTime = TimeSpan.MaxValue }).Keys);
        Assert.Contains("type", Validate(new AddAdvancedCcRuleRequest { Name = "test", Type = "unknown" }).Keys);
        Assert.Contains("action", Validate(new UpdateAdvancedCcRuleRequest { Id = "test", Action = "999" }).Keys);
        Assert.Empty(Validate(new AddAdvancedCcRuleRequest { Name = "test", Type = "frequentaccess", Action = "captcha" }));
    }

    [Fact]
    public void WafConditionsReportNestedInvalidFields()
    {
        var rule = new WafCustomRule { Name = "test", ConditionGroups = [new() { Conditions = [new()
            { Field = WafMatchField.Header, Operator = WafMatchOperator.Regex, Value = "[" }] }] };
        var fields = Validate(rule);
        Assert.Contains("conditionGroups[0].conditions[0].fieldName", fields.Keys);
        Assert.Contains("conditionGroups[0].conditions[0].value", fields.Keys);
        rule.ConditionGroups[0].Conditions[0].FieldName = "X-Test";
        rule.ConditionGroups[0].Conditions[0].Value = "test";
        Assert.Empty(Validate(rule));
    }

    [Fact]
    public void WafGroupsRejectEmptyAndExcessiveConditions()
    {
        Assert.Contains("conditionGroups", Validate(new WafCustomRule { Name = "test" }).Keys);
        var rule = new WafCustomRule { Name = "test", ConditionGroups = Enumerable.Range(0, 3)
            .Select(_ => new WafConditionGroup { Conditions = Enumerable.Range(0, 7).Select(_ => new WafCondition()).ToList() }).ToList() };
        Assert.Contains("conditionGroups", Validate(rule).Keys);
    }

    [Fact]
    public void NotificationsAndSitesReportSpecificInvalidFields()
    {
        var smtp = new LyWaf.Services.Notifications.NotificationChannel("test", "Test", "smtp", "invalid", SmtpHost: "https://mail", Sender: "invalid");
        var fields = Validate(smtp);
        Assert.Contains("smtpHost", fields.Keys);
        Assert.Contains("sender", fields.Keys);
        Assert.Contains("destination", fields.Keys);
        Assert.Contains("destination", Validate(smtp with { Kind = "webhook", Destination = "https://example.com/#fragment" }).Keys);
        Assert.Empty(Validate(smtp with { Kind = "webhook", Destination = "https://example.com/hook" }));
        var site = new LyWaf.Services.Sites.ManagedSite("site", "Site", new Yarp.ReverseProxy.Configuration.RouteConfig
            { RouteId = "other", Match = new() { Path = "/{**path}" } }, RuleIds: [""]);
        fields = Validate(site);
        Assert.Contains("route.routeId", fields.Keys);
        Assert.Contains("ruleIds[0]", fields.Keys);
    }

    [Fact]
    public void AntiBotExceptionsRejectUnsupportedCidrsAndEmptyPrefixes()
    {
        var request = new LyWaf.Control.Endpoints.AntiBotUpdateRequest { BypassIps = ["192.0.2.0/24"], BypassPaths = [""], TrustedFingerprintProxies = ["invalid"] };
        var fields = Validate(request);
        Assert.Contains("bypassIps[0]", fields.Keys);
        Assert.Contains("bypassPaths[0]", fields.Keys);
        Assert.Contains("trustedFingerprintProxies[0]", fields.Keys);
        Assert.Empty(Validate(new LyWaf.Control.Endpoints.AntiBotUpdateRequest { BypassIps = ["192.0.2.1"], BypassPaths = ["/health"] }));
    }

    [Fact]
    public async Task NullRequiredFieldsAreRejectedBeforeBusinessExecution()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AcceptsMetadata(["application/json"], typeof(Request))), "test"));
        var invocation = new DefaultEndpointFilterInvocationContext(context, new Request(null!, [new Child(null!), null!]));
        var called = false;
        var result = await new ManagementErrorFilter().InvokeAsync(invocation, _ => { called = true; return ValueTask.FromResult<object?>(Results.Ok()); });
        Assert.False(called);
        var error = Assert.IsType<ApiErrorEnvelope>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        var fields = Assert.IsType<Dictionary<string, string[]>>(error.Error.Details);
        Assert.Contains("name", fields.Keys);
        Assert.Contains("children[0].name", fields.Keys);
        Assert.Contains("children[1]", fields.Keys);
        Assert.DoesNotContain("note", fields.Keys);
    }
}
