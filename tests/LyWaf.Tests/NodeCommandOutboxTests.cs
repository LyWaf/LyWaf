using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeCommandOutboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "outbox-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DistributionNodeClient Client(Transport transport) => new(
        new StaticOptionsMonitor<DistributionOptions>(new()
        {
            Nodes = new() { ["edge"] = new() { Endpoint = "https://edge", ProtocolVersion = 2 } }
        }), transport, new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "control", StateDirectory = _directory,
            Peers = new() { ["edge"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) } } }
        }));

    [Fact]
    public async Task LostResponseQueriesOriginalIdentityWithFreshNonceAndCachesResult()
    {
        using var transport = new Transport((message, count) => count == 1
            ? throw new HttpRequestException("response lost") : Ack(message));
        var result = await Client(transport).CommandAsync("edge", "commit", new("deployment"), default);
        Assert.True(result.Ack);
        Assert.Equal("configuration.commit", transport.Messages[0].Type);
        Assert.Equal("configuration.result", transport.Messages[1].Type);
        Assert.Equal(transport.Messages[0].MessageId, transport.Messages[1].Payload.Deserialize<NodeMessage>(Json)!.MessageId);
        Assert.NotEqual(transport.Nonces[0], transport.Nonces[1]);
        var restarted = Client(transport);
        Assert.Equal(result, await restarted.RecoverAsync(transport.Messages[0].MessageId, default));
        Assert.Equal(2, transport.Messages.Count);
        Assert.Equal("Completed", Assert.Single(restarted.ReadOutbox()).State);
    }

    [Theory]
    [InlineData(409, NodeConfigurationErrors.Conflict)]
    [InlineData(422, NodeConfigurationErrors.Digest)]
    [InlineData(200, NodeConfigurationErrors.Invalid)]
    public async Task BusinessNackIsBoundedSanitizedAndDurablyReplayedWithoutQuery(int status, string code)
    {
        using var transport = new Transport((message, _) => new((HttpStatusCode)status)
        {
            Content = JsonContent.Create(new NodeAcknowledgement(false, "edge", "deployment", "commit",
                Error: "secret path and full remote exception", ErrorCode: code, MessageKey: "untrusted.key"))
        });
        var client = Client(transport);
        var result = await client.CommandAsync("edge", "commit", new("deployment"), default);
        Assert.False(result.Ack);
        Assert.Equal(code, result.Error);
        Assert.Equal("node.errors." + code, result.MessageKey);
        var stored = Assert.Single(client.ReadOutbox());
        Assert.Equal("Rejected", stored.State);
        Assert.Equal(code, stored.ErrorCode);
        Assert.Equal(result, await Client(transport).RecoverAsync(stored.Message.MessageId, default));
        Assert.Single(transport.Messages);
        Assert.DoesNotContain("secret path", File.ReadAllText(Directory.GetFiles(Path.Combine(_directory, "outbox"), "*.json").Single()));
    }

    [Theory]
    [InlineData("wrong", NodeConfigurationErrors.Conflict, false)]
    [InlineData("edge", NodeConfigurationErrors.Unknown, false)]
    [InlineData("edge", NodeConfigurationErrors.Digest, false)]
    [InlineData("edge", NodeConfigurationErrors.Conflict, true)]
    public async Task InvalidBusinessRejectionCannotFabricateDefiniteOutcome(string node, string code, bool ack)
    {
        using var transport = new Transport((_, _) => new(HttpStatusCode.Conflict)
        { Content = JsonContent.Create(new NodeAcknowledgement(ack, node, "deployment", "commit", ErrorCode: code)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Equal("Unknown", Assert.Single(Client(transport).ReadOutbox()).State);
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task ProtocolUnknown409StopsWithoutFabricatingBusinessNackOrRetrying()
    {
        using var transport = new Transport((_, _) => new(HttpStatusCode.Conflict)
        { Content = JsonContent.Create(new { errorCode = "node_outcome_unknown" }) });
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        var record = Assert.Single(Client(transport).ReadOutbox());
        Assert.Equal("Stopped", record.State);
        Assert.Null(record.Result);
        Assert.Equal("node_outcome_unknown", record.ErrorCode);
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task OversizedBusinessRejectionCannotEnterPersistentResult()
    {
        using var transport = new Transport((_, _) => new(HttpStatusCode.Conflict)
        { Content = JsonContent.Create(new NodeAcknowledgement(false, "edge", "deployment", "commit",
            Error: new string('s', NodeRemoteError.MaximumBodyBytes), ErrorCode: NodeConfigurationErrors.Conflict)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Null(Assert.Single(Client(transport).ReadOutbox()).Result);
        Assert.Single(transport.Messages);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task PermanentRejectionIsDurableAndNeverRetried(int status)
    {
        using var transport = new Transport((_, _) => new((HttpStatusCode)status));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task CancellationAfterSendRecoversByQueryFromNewClient()
    {
        using var cancel = new CancellationTokenSource();
        using var transport = new Transport((message, count) =>
        {
            if (count != 1) return Ack(message);
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), cancel.Token));
        var restarted = Client(transport);
        var record = Assert.Single(restarted.ReadOutbox());
        Assert.Equal("Unknown", record.State);
        Assert.True((await restarted.RecoverAsync(record.Message.MessageId, default)).Ack);
        Assert.Equal("configuration.result", transport.Messages[1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedAndStoppedMetadataSurvivesRestartWithoutRemoteText(bool queryFailure)
    {
        using var transport = new Transport((_, count) =>
        {
            if (queryFailure && count == 1) throw new HttpRequestException("lost");
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new { errorCode = "peer.denied", traceId = "trace:123", message = "SECRET_REMOTE_BODY" })
            };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(12));
            return response;
        });
        var first = await Assert.ThrowsAsync<HttpRequestException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        var restarted = Client(transport);
        var record = Assert.Single(restarted.ReadOutbox());
        Assert.Equal(queryFailure ? "Stopped" : "Rejected", record.State);
        var replay = await Assert.ThrowsAsync<HttpRequestException>(() => restarted.CommandAsync("edge", "commit", new("deployment"), default));
        var recovery = await Assert.ThrowsAsync<HttpRequestException>(() => restarted.RecoverAsync(record.Message.MessageId, default));
        foreach (var error in new[] { first, replay, recovery })
        {
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.Equal("peer.denied", error.Data["ErrorCode"]);
            Assert.Equal("node.errors.peer.denied", error.Data["MessageKey"]);
            Assert.Equal("trace:123", error.Data["RemoteTraceId"]);
            Assert.Equal(TimeSpan.FromSeconds(12), error.Data["RetryAfter"]);
        }
        Assert.Equal(queryFailure ? 2 : 1, transport.Messages.Count);
        var persisted = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "outbox"), "*.json")));
        Assert.DoesNotContain("SECRET_REMOTE_BODY", persisted);
    }

    [Fact]
    public async Task OutboxOnlyPersistsAllowlistedBoundedAsciiMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "d",
            "configuration.commit", now, now.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "d" }));
        var error = new HttpRequestException("SECRET_FULL_ERROR", null, HttpStatusCode.Conflict);
        error.Data["ErrorCode"] = new string('x', 129);
        error.Data["MessageKey"] = "bad\nkey";
        error.Data["RemoteTraceId"] = "非ASCII";
        error.Data["Body"] = "SECRET_FULL_ERROR";
        await Assert.ThrowsAsync<HttpRequestException>(() => new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://edge",
            (_, _, _) => throw error, default));
        var replay = await Assert.ThrowsAsync<HttpRequestException>(() => new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://edge",
            (_, _, _) => throw new Exception("must not send"), default));
        Assert.Empty(replay.Data);
        var persisted = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "outbox"), "*.json")));
        Assert.DoesNotContain("SECRET_FULL_ERROR", persisted);
        Assert.Null(Assert.Single(new NodeCommandOutbox(_directory).ReadRecords()).ErrorCode);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task TransientFailuresHaveDurableBoundedBudget(int status)
    {
        using var transport = new Transport((_, _) => new((HttpStatusCode)status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Equal(3, transport.Messages.Count);
        Assert.All(transport.Messages.Skip(1), m => Assert.Equal("configuration.result", m.Type));
    }

    [Fact]
    public async Task DifferentPayloadCannotReuseBusinessIdentity()
    {
        using var transport = new Transport((message, _) => Ack(message));
        await Client(transport).PrepareAsync("edge", new("deployment", "v", "hash", "first"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(transport).PrepareAsync("edge", new("deployment", "v", "hash", "second"), default));
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task RetryAfterIsPersistedAndCallerCanCancelBackoff()
    {
        using var cancel = new CancellationTokenSource();
        using var transport = new Transport((_, _) =>
        {
            cancel.CancelAfter(100);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
            return response;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), cancel.Token));
        var record = Assert.Single(Client(transport).ReadOutbox());
        Assert.True(record.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(50));
        Assert.Equal(1, record.Attempts);
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task ExpiredRecordCannotResetDeadlineOrSendAfterRestart()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "d",
            "configuration.commit", now.AddMinutes(-6), now.AddMinutes(-1), JsonSerializer.SerializeToElement(new { deploymentId = "d" }));
        Task<NodeAcknowledgement> Send(NodeMessage _, bool query, CancellationToken token) => throw new Exception("must not send");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://edge", Send, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NodeCommandOutbox(_directory).ExecuteAsync(
            message with { CreatedAt = now, ExpiresAt = now.AddMinutes(5) }, "https://edge", Send, default));
        Assert.Equal(message.ExpiresAt, Assert.Single(new NodeCommandOutbox(_directory).ReadRecords()).Message.ExpiresAt);
    }

    [Fact]
    public async Task PreCancelledCallCreatesNoRecordAndSendsNothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var transport = new Transport((_, _) => throw new Exception("must not send"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(transport).CommandAsync("edge", "commit", new("deployment"), cancel.Token));
        Assert.Empty(Client(transport).ReadOutbox());
    }

    [Fact]
    public async Task MissingInboxQueryNeverExecutesOrCreatesRecord()
    {
        var inbox = new NodeCommandInbox(new StaticOptionsMonitor<NodeCommunicationOptions>(new() { StateDirectory = _directory }));
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "d",
            "configuration.commit", now, now.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "d" }));
        var result = await inbox.ExecuteAsync(message, () => throw new Exception("must not execute"), default, queryOnly: true);
        Assert.Equal(409, result.Error!.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"));
    }

    private static HttpResponseMessage Ack(NodeMessage message)
    {
        var original = message.Type == "configuration.result" ? message.Payload.Deserialize<NodeMessage>(Json)! : message;
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new NodeAcknowledgement(true,
            original.ReceiverNodeId, original.CorrelationId, original.Type["configuration.".Length..])) };
    }

    private sealed class Transport(Func<NodeMessage, int, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<NodeMessage> Messages { get; } = [];
        public List<string> Nonces { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var message = (await request.Content!.ReadFromJsonAsync<NodeMessage>(Json, cancellationToken))!;
            Messages.Add(message);
            Nonces.Add(request.Headers.GetValues("X-LyWaf-Nonce").Single());
            return respond(message, Messages.Count);
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
