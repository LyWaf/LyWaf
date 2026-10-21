using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LyWaf.Services.Captcha;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class CaptchaAuthorityHttpTests
{
    [Fact]
    public async Task SignedProductionRoutesCannotReturnConsumedAnswerAgainEvenWithSameMessageId()
    {
        await using var host = await NodeContractHttpHost.Start(configure: (services, fixture) =>
        {
            services.AddSingleton<IOptions<CaptchaStoreOptions>>(Options.Create(new CaptchaStoreOptions
            { Provider = "Authority", ClusterId = "contract", AuthorityNodeId = "receiver", StateDirectory = Path.Combine(fixture.Directory, "challenges") }));
            services.AddSingleton<FileCaptchaAuthority>(); services.AddSingleton<AuthorityCaptchaStore>();
        });
        var token = "r1." + Guid.NewGuid().ToString("N");
        using var issued = await host.Send(AuthorityCaptchaStore.IssueOperation, new CaptchaIssueRequest(token,
            new() { Mode = "math", Answer = "42", ClientIp = "192.0.2.1" }, DateTimeOffset.UtcNow, 300000));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        using var first = await host.Send(AuthorityCaptchaStore.TakeOperation, new CaptchaTakeRequest(token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("42", body.GetProperty("entry").GetProperty("answer").GetString());
        var originalId = body.GetProperty("requestId").GetString()!;
        var message = new LyWaf.Services.NodeCommunication.NodeMessage(2, "contract", "sender", "receiver", originalId,
            "contract-operation", AuthorityCaptchaStore.TakeOperation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            JsonSerializer.SerializeToElement(new CaptchaTakeRequest(token), LyWaf.Services.NodeCommunication.NodeMessageAuthentication.JsonOptions));
        using var repeated = await host.Send(message);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var replay = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, replay.GetProperty("entry").ValueKind);
        using var duplicateIssue = await host.Send(AuthorityCaptchaStore.IssueOperation, new CaptchaIssueRequest(token,
            new() { Answer = "42", ClientIp = "192.0.2.1" }, DateTimeOffset.UtcNow, 300000));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateIssue.StatusCode);
        using var expired = await host.Send(AuthorityCaptchaStore.IssueOperation, new CaptchaIssueRequest(
            "r1." + Guid.NewGuid().ToString("N"), new(), DateTimeOffset.UtcNow.AddMinutes(-2), 300000));
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
    }
}
