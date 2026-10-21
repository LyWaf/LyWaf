using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeBusinessErrorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "business-errors-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldPath = SharedData.ConfigFilePath;
    private readonly IConfigurationRoot _root;
    private DistributionNodeService Node() => new(new StaticOptionsMonitor<DistributionOptions>(new()
        { NodeId = "edge", StateDirectory = _directory }), new(new HttpContextAccessor()), new(), _root);
    private static PrepareConfigurationRequest Request(string content = "SpeedLimit:\n  Value: new\n") =>
        new("d", "v", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content);

    public NodeBusinessErrorTests()
    {
        Directory.CreateDirectory(_directory);
        SharedData.ConfigFilePath = Path.Combine(_directory, "private-secret-config.yaml");
        File.WriteAllText(SharedData.ConfigFilePath, "SpeedLimit:\n  Value: old\n");
        _root = new ConfigurationBuilder().AddYamlFile(SharedData.ConfigFilePath, false, false).Build();
    }

    [Fact]
    public async Task DigestAndBaselineHaveStableDistinctCodesAndLegacyError()
    {
        var node = Node();
        var digest = await node.PrepareAsync(Request() with { Sha256 = "bad" }, default);
        Assert.Equal(NodeConfigurationErrors.Digest, digest.ErrorCode);
        Assert.Equal(422, NodeConfigurationErrors.HttpStatus(digest.ErrorCode));
        Assert.NotEmpty(digest.Error!);
        Assert.True((await node.PrepareAsync(Request(), default)).Ack);
        File.WriteAllText(SharedData.ConfigFilePath, "SpeedLimit:\n  Value: local\n");
        var baseline = await node.CommitAsync("d", default);
        Assert.Equal(NodeConfigurationErrors.Conflict, baseline.ErrorCode);
        Assert.Equal(409, NodeConfigurationErrors.HttpStatus(baseline.ErrorCode));
        Assert.Equal("node.errors." + baseline.ErrorCode, baseline.MessageKey);
    }

    [Theory]
    [InlineData("SpeedLimit: [secret-parser-value")]
    [InlineData("DistributionCompatibility:\n  SchemaVersion: 99\n")]
    public async Task SyntaxFailureDoesNotLeakParserInputOrException(string content)
    {
        var result = await Node().PrepareAsync(Request(content), default);
        Assert.False(result.Ack);
        Assert.Equal(NodeConfigurationErrors.Invalid, result.ErrorCode);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret-parser-value", json);
        Assert.DoesNotContain("private-secret", json);
    }

    [Fact]
    public async Task RealIoFailureBeforeIntentHasStableCodeWithoutPath()
    {
        File.WriteAllText(Path.Combine(_directory, "prepared"), "blocking directory creation");
        var result = await Node().PrepareAsync(Request(), default);
        Assert.Equal(NodeConfigurationErrors.Storage, result.ErrorCode);
        Assert.DoesNotContain(_directory, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    [InlineData("rollback")]
    public async Task CancellationCannotBecomeBusinessNack(string stage)
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var node = Node();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage switch
        {
            "prepare" => node.PrepareAsync(Request(), cancel.Token),
            "commit" => node.CommitAsync("d", cancel.Token),
            _ => node.RollbackAsync("d", cancel.Token)
        });
    }

    [Fact]
    public async Task IoAfterRollbackEffectRemainsUnknownInRealInbox()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request(), default)).Ack);
        Assert.True((await node.CommitAsync("d", default)).Ack);
        node.RollbackFaultPoint = stage => { if (stage == "ConfigurationWritten") throw new IOException("secret disk path"); };
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "d",
            "configuration.rollback", now, now.AddMinutes(5), JsonSerializer.SerializeToElement(new DistributionCommandRequest("d")));
        var inbox = new NodeCommandInbox(new StaticOptionsMonitor<NodeCommunicationOptions>(new() { StateDirectory = _directory }));
        await Assert.ThrowsAsync<IOException>(() => inbox.ExecuteAsync(message,
            () => node.RollbackAsync("d", default), default));
        var query = await inbox.ExecuteAsync(message, () => throw new Exception("must not execute"), default, queryOnly: true);
        Assert.Null(query.Acknowledgement);
        Assert.Equal("node_outcome_unknown", query.Error?.Code);
    }

    [Fact]
    public async Task V1ClientKeepsLegacyErrorWithoutRequiringNewMetadata()
    {
        using var transport = new LegacyTransport();
        var client = new DistributionNodeClient(new StaticOptionsMonitor<DistributionOptions>(new()
        {
            SharedSecret = new string('k', 32), Nodes = new() { ["edge"] = new() { Endpoint = "https://edge", ProtocolVersion = 1 } }
        }), transport);
        var result = await client.CommandAsync("edge", "commit", new("d"), default);
        Assert.False(result.Ack);
        Assert.Equal("legacy error", result.Error);
        Assert.Null(result.ErrorCode);
    }

    private sealed class LegacyTransport : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
            { Content = System.Net.Http.Json.JsonContent.Create(new NodeAcknowledgement(false, "edge", "d", "commit", Error: "legacy error")) });
    }

    public void Dispose()
    {
        (_root as IDisposable)?.Dispose();
        SharedData.ConfigFilePath = _oldPath;
        Directory.Delete(_directory, true);
    }
}
