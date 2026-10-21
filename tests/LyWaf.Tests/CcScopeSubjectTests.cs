using System.Security.Claims;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcScopeSubjectTests
{
    [Fact]
    public void ExplicitScopesPreserveDefaultSiteIsolation()
    {
        var rule = new AdvancedCcRule { Id = "rule", Action = CcAction.Block };
        string Key(string host, string node) => CcRuleStateKey.Create(rule, "192.0.2.1", CcRuleStateKey.ResolveScope(rule, host, node));
        Assert.Equal(Key("host:a", "a"), Key("host:a", "b"));
        Assert.NotEqual(Key("host:a", "a"), Key("host:b", "a"));
        rule.Scope = CcScope.Node;
        Assert.NotEqual(Key("host:a", "a"), Key("host:a", "b"));
        Assert.Equal(Key("host:a", "a"), Key("host:b", "a"));
        rule.Scope = CcScope.Cluster;
        Assert.Equal(Key("host:a", "a"), Key("host:b", "b"));
    }

    [Theory]
    [InlineData(CcSubjectKind.Account, ClaimTypes.NameIdentifier)]
    [InlineData(CcSubjectKind.ApiCredential, "lywaf:api-credential-id")]
    public void OnlyConfiguredAuthenticatedIssuerSelectsSubject(CcSubjectKind kind, string claim)
    {
        var rule = new AdvancedCcRule { Action = CcAction.Block, Subject = kind,
            SubjectAuthenticationType = "server", SubjectIssuer = "authority" };
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Account-Id"] = "victim";
        Assert.False(CcRequestIdentity.TrySubject(context, rule, "192.0.2.1", out _));
        context.User = new(new ClaimsIdentity([new Claim(claim, "victim", ClaimValueTypes.String, "authority")]));
        Assert.False(CcRequestIdentity.TrySubject(context, rule, "192.0.2.1", out _));
        context.User = new(new ClaimsIdentity([new Claim(claim, "victim", ClaimValueTypes.String, "forged")], "server"));
        Assert.False(CcRequestIdentity.TrySubject(context, rule, "192.0.2.1", out _));
        context.User = new(new ClaimsIdentity([new Claim(claim, "victim", ClaimValueTypes.String, "authority")], "server"));
        Assert.True(CcRequestIdentity.TrySubject(context, rule, "192.0.2.1", out var subject));
        Assert.DoesNotContain("victim", subject);
        Assert.True(CcRequestIdentity.ValidSubject(rule, subject));
        Assert.True(CcRequestIdentity.TrySubject(context, rule, "192.0.2.2", out var otherIp));
        Assert.Equal(subject, otherIp);
        context.User.AddIdentity(new ClaimsIdentity([new Claim(claim, "other", ClaimValueTypes.String, "authority")], "server"));
        Assert.False(CcRequestIdentity.TrySubject(context, rule, "192.0.2.1", out _));
        rule.Action = CcAction.Captcha;
        Assert.False(CcRequestIdentity.ValidConfiguration(rule));
    }

    [Fact]
    public void InvalidScopesAndAmbiguousMembershipCannotSilentlyUseDefault()
    {
        Assert.False(CcRequestIdentity.ValidConfiguration(new() { Scope = (CcScope)99 }));
        Assert.False(CcRequestIdentity.ValidConfiguration(new() { Scope = CcScope.Cluster, ProtectionGroup = "g" }));
        Assert.False(CcRequestIdentity.ValidConfiguration(new() { RequireCompleteCoverage = true }));
        Assert.False(CcRequestIdentity.ValidConfiguration(new() { ExpectedNodes = ["a", "a"] }));
        Assert.False(CcRequestIdentity.ValidConfiguration(new() { ExpectedNodes = null! }));
    }

    [Fact]
    public void ManagedMembershipRequiresSharedScopeCompleteCoverageAndNoManualList()
    {
        var rule = new AdvancedCcRule { UseManagedMembership = true, RequireCompleteCoverage = true };
        Assert.True(CcRequestIdentity.ValidConfiguration(rule));
        var managed = CcRuleStateKey.Revision(rule);
        rule.UseManagedMembership = false;
        var manual = CcRuleStateKey.Revision(rule);
        Assert.NotEqual(manual, managed);
        rule.UseManagedMembership = true;
        Assert.Equal(managed, CcRuleStateKey.Revision(rule));
        rule.ExpectedNodes = ["a"];
        Assert.False(CcRequestIdentity.ValidConfiguration(rule));
        rule.ExpectedNodes = [];
        rule.Scope = CcScope.Node;
        Assert.False(CcRequestIdentity.ValidConfiguration(rule));
        rule.Scope = CcScope.Cluster;
        Assert.True(CcRequestIdentity.ValidConfiguration(rule));
        rule.RequireCompleteCoverage = false;
        Assert.False(CcRequestIdentity.ValidConfiguration(rule));
    }
}
