using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LyWaf.Services.Auth;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeReconciliationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "reconciliation-" + Guid.NewGuid().ToString("N"));
    private readonly Client _client = new();
    private readonly StaticOptionsMonitor<DistributionOptions> _options;
    private readonly StaticOptionsMonitor<NodeCommunicationOptions> _protocol;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public NodeReconciliationTests()
    {
        _options = new(new()
        {
            Enabled = true, Role = "Controller", Nodes = new() { ["edge"] = new() { Endpoint = "https://edge", ProtocolVersion = 2 } },
            Permissions = new() { NodeOperators = new() { ["edge"] = ["alice"], ["other"] = ["bob"] } }
        });
        _protocol = new(new()
        {
            Enabled = true, Role = "Controller", NodeId = "control", ClusterId = "cluster", StateDirectory = _directory,
            Peers = new() { ["edge"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) } } }
        });
    }
    private NodeReconciliationService Service(IDistributionNodeClient? client = null) => new(client ?? _client,
        new MultiNodeDashboardService(_options, client ?? _client), _options, _protocol);
    private NodeReconciliationRequest Request(string? assessment = null) => new(Guid.NewGuid().ToString("D"),
        Service().Get("alice", "edge", _client.Record.Message.MessageId).Identity.Digest, "Ticket OPS-123 verified", assessment);
    private Task<NodeReconciliationAction> Query(NodeReconciliationRequest request) => Service().QueryAsync("alice", "127.0.0.1",
        "edge", _client.Record.Message.MessageId, request, default);
    private string JournalPath => Assert.Single(Directory.GetFiles(Path.Combine(_directory, "reconciliation"), "*.json"));
    private void EditJournal(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(JournalPath))!.AsObject();
        edit(json);
        File.WriteAllText(JournalPath, json.ToJsonString());
    }

    [Fact]
    public async Task QueryAfterExpiryKeepsOutboxUnknownAndPersistsIndependentEvidenceAcrossRestart()
    {
        var original = JsonSerializer.Serialize(_client.Record);
        var request = Request();
        _client.Query = (_, _) =>
        {
            Assert.Contains("Requested", File.ReadAllText(JournalPath));
            return Task.FromResult(Ack());
        };
        var result = await Query(request);
        Assert.Equal("Observed", result.State);
        Assert.True(result.ObservedNodeResult!.Ack);
        Assert.Equal(original, JsonSerializer.Serialize(_client.Record));
        var restarted = Service();
        Assert.Equal(result, await restarted.QueryAsync("alice", "127.0.0.1", "edge", _client.Record.Message.MessageId, request, default));
        Assert.Equal(1, _client.Queries);
        var view = restarted.Get("alice", "edge", _client.Record.Message.MessageId);
        Assert.Equal("Unknown", view.OutboxState);
        Assert.Null(view.OutboxResult);
        Assert.Equal("alice", Assert.Single(view.Actions).Actor);
        Assert.DoesNotContain("SECRET_CONFIG", JsonSerializer.Serialize(view));
        Assert.DoesNotContain("SECRET_CONFIG", File.ReadAllText(JournalPath));
        Assert.Equal(0, _client.Commands);
    }

    [Theory]
    [InlineData("OperatorBelievesApplied")]
    [InlineData("OperatorBelievesNotApplied")]
    [InlineData("NeedsIntervention")]
    [InlineData("Unresolved")]
    public async Task AssessmentNeverSynthesizesAckOrExecutesCommand(string assessment)
    {
        var result = await Service().AssessAsync("alice", "127.0.0.1", "edge", _client.Record.Message.MessageId, Request(assessment), default);
        Assert.Equal("Recorded", result.State);
        Assert.Equal(assessment, result.Assessment);
        Assert.Null(result.ObservedNodeResult);
        Assert.Null(_client.Record.Result);
        Assert.Equal("Unknown", _client.Record.State);
        Assert.Equal(0, _client.Queries + _client.Commands);
    }

    [Fact]
    public async Task PermissionAndOriginalIdentityAreCheckedBeforeAuditOrNetwork()
    {
        var request = Request();
        await Assert.ThrowsAsync<NodeReconciliationAccessDeniedException>(() => Service().QueryAsync("bob", "ip", "edge", _client.Record.Message.MessageId, request, default));
        await Assert.ThrowsAsync<NodeReconciliationAccessDeniedException>(() => Service().QueryAsync("", "ip", "edge", _client.Record.Message.MessageId, request, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(request with { Digest = new string('A', 64) }));
        _client.Record = _client.Record with { Message = _client.Record.Message with { ClusterId = "different" } };
        Assert.Empty(Service().List("alice", "edge"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Query(request));
        Assert.Equal(0, _client.Queries);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "reconciliation"), "*.json"));
    }

    [Theory]
    [InlineData("Pending", 0, false)]
    [InlineData("Rejected", 1, false)]
    [InlineData("Unknown", 1, false)]
    [InlineData("Completed", 1, true)]
    public async Task OnlyUnresolvedStoppedExpiredOrExhaustedCommandsAreActionable(string state, int attempts, bool completed)
    {
        _client.Record = _client.Record with { State = state, Attempts = attempts, Result = completed ? Ack() : null,
            Message = _client.Record.Message with { CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(Request()));
        Assert.Equal(0, _client.Queries);
    }

    [Theory]
    [InlineData("Stopped", 1)]
    [InlineData("Unknown", 3)]
    public async Task StoppedAndExhaustedCommandsCanBeQueriedWithoutResettingBudget(string state, int attempts)
    {
        _client.Record = _client.Record with { State = state, Attempts = attempts,
            Message = _client.Record.Message with { CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) } };
        Assert.Equal("Observed", (await Query(Request())).State);
        Assert.Equal(attempts, _client.Record.Attempts);
        Assert.Equal(state, _client.Record.State);
    }

    [Fact]
    public async Task CooldownBudgetAndRequestIdPreventRepeatedQueries()
    {
        var request = Request();
        await Query(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(request with { Reason = "different" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(request with { RequestId = Guid.NewGuid().ToString("D") }));
        EditJournal(json =>
        {
            var template = json["Actions"]![0]!.DeepClone();
            template["RequestedAt"] = DateTimeOffset.UtcNow.AddHours(-1);
            var actions = new JsonArray();
            for (var i = 0; i < NodeReconciliationService.MaximumQueries; i++)
            {
                var item = template.DeepClone();
                item["RequestId"] = Guid.NewGuid().ToString("D");
                actions.Add(item);
            }
            json["Actions"] = actions;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(Request()));
        Assert.Equal(1, _client.Queries);
    }

    [Fact]
    public async Task AuditFailureBeforeSendPreventsNetworkAndInterruptedCompletionNeverResends()
    {
        var request = Request();
        var directory = Path.Combine(_directory, "reconciliation");
        var key = Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(directory, "*.lock")));
        Directory.CreateDirectory(Path.Combine(directory, key + ".json.tmp"));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Query(request));
        Assert.Equal(0, _client.Queries);
        Directory.Delete(Path.Combine(directory, key + ".json.tmp"));
        _client.Query = (_, _) =>
        {
            Directory.CreateDirectory(JournalPath + ".tmp");
            return Task.FromResult(Ack());
        };
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Query(request));
        Directory.Delete(JournalPath + ".tmp");
        Assert.Equal("Requested", (await Query(request)).State);
        Assert.Equal(1, _client.Queries);
        File.WriteAllText(JournalPath, "{");
        await Assert.ThrowsAsync<JsonException>(() => Query(request));
        Assert.Equal(1, _client.Queries);
    }

    [Fact]
    public async Task CancellationPersistsAuditAndSameCommandCannotRunConcurrently()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Query = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Ack(); };
        var request = Request();
        using var stop = new CancellationTokenSource();
        var running = Service().QueryAsync("alice", "ip", "edge", _client.Record.Message.MessageId, request, stop.Token);
        await entered.Task;
        try { await Assert.ThrowsAnyAsync<IOException>(() => Query(request)); }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
        Assert.Equal("Cancelled", (await Query(request)).State);
        Assert.Equal(1, _client.Queries);
    }

    [Fact]
    public async Task RemoteFailuresPersistOnlyBoundedDiagnosticsAndWrongAckIsNotAccepted()
    {
        var error = new HttpRequestException("SECRET_REMOTE_BODY", null, HttpStatusCode.Conflict);
        error.Data["ErrorCode"] = "node_result_retired";
        error.Data["RemoteTraceId"] = "trace:1";
        _client.Query = (_, _) => throw error;
        var result = await Query(Request());
        Assert.Equal("Unconfirmed", result.State);
        Assert.Equal("node_result_retired", result.ErrorCode);
        Assert.Equal(409, result.StatusCode);
        Assert.DoesNotContain("SECRET_REMOTE_BODY", File.ReadAllText(JournalPath));
        EditJournal(json => json["Actions"]![0]!["RequestedAt"] = DateTimeOffset.UtcNow.AddMinutes(-2));
        _client.Query = (_, _) => Task.FromResult(Ack() with { NodeId = "wrong" });
        var invalid = await Query(Request());
        Assert.Equal("Unconfirmed", invalid.State);
        Assert.Null(invalid.ObservedNodeResult);
    }

    [Fact]
    public async Task ActualClientSendsOnlySignedResultQueryWithOriginalExpiredEnvelopeAndFreshNonce()
    {
        using var transport = new Transport();
        var client = new DistributionNodeClient(_options, transport, _protocol);
        var original = _client.Record;
        await client.QueryRecordedOutcomeAsync(original, default);
        await client.QueryRecordedOutcomeAsync(original, default);
        Assert.Equal(2, transport.Messages.Count);
        Assert.All(transport.Messages, m =>
        {
            Assert.Equal("configuration.result", m.Type);
            Assert.True(m.ExpiresAt > DateTimeOffset.UtcNow);
            var embedded = m.Payload.Deserialize<NodeMessage>(Json)!;
            Assert.Equal(original.Message.MessageId, embedded.MessageId);
            Assert.Equal(original.Message.ExpiresAt, embedded.ExpiresAt);
        });
        Assert.NotEqual(transport.Nonces[0], transport.Nonces[1]);
        Assert.NotEqual(transport.Messages[0].MessageId, transport.Messages[1].MessageId);
        Assert.Empty(client.ReadOutbox());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryRecordedOutcomeAsync(original with { Endpoint = "https://changed" }, default));
        Assert.Equal(2, transport.Messages.Count);
    }

    [Fact]
    public async Task ActualServiceAndClientUsePersistentExpiredOutboxWithoutChangingIt()
    {
        var directory = Path.Combine(_directory, "outbox");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "seed.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_client.Record));
        var before = File.ReadAllBytes(path);
        using var transport = new Transport();
        var client = new DistributionNodeClient(_options, transport, _protocol);
        var service = Service(client);
        var identity = service.Get("alice", "edge", _client.Record.Message.MessageId).Identity;
        var action = await service.QueryAsync("alice", "127.0.0.1", "edge", identity.MessageId,
            new(Guid.NewGuid().ToString("D"), identity.Digest, "Manual inspection OPS-456"), default);
        Assert.Equal("Observed", action.State);
        Assert.Single(transport.Messages);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("Unknown", Assert.Single(client.ReadOutbox()).State);
        Assert.Equal(3, Assert.Single(client.ReadOutbox()).Attempts);
        Assert.Single(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task AuditQuotaAndActionCapFailClosed()
    {
        var request = Request();
        var filler = Path.Combine(_directory, "reconciliation", "capacity.bin");
        using (var file = File.Create(filler)) file.SetLength(64 * 1024 * 1024);
        await Assert.ThrowsAnyAsync<IOException>(() => Query(request));
        Assert.Equal(0, _client.Queries);
        File.Delete(filler);
        await Service().AssessAsync("alice", "ip", "edge", _client.Record.Message.MessageId, request with { Assessment = "Unresolved" }, default);
        EditJournal(json =>
        {
            var template = json["Actions"]![0]!.DeepClone();
            var actions = new JsonArray();
            for (var i = 0; i < NodeReconciliationService.MaximumActions; i++)
            {
                var item = template.DeepClone();
                item["RequestId"] = Guid.NewGuid().ToString("D");
                actions.Add(item);
            }
            json["Actions"] = actions;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(Request()));
        Assert.Equal(0, _client.Queries);
    }

    [Fact]
    public async Task SlowCommandDoesNotHoldGlobalAuditLockAndOtherPeerCanQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Query = async (record, _) =>
        {
            if (record.Message.ReceiverNodeId == "edge") { entered.SetResult(); await release.Task; }
            return Ack() with { NodeId = record.Message.ReceiverNodeId };
        };
        var first = Query(Request());
        await entered.Task;
        try
        {
            _client.Record = _client.Record with { Message = _client.Record.Message with { ReceiverNodeId = "other", MessageId = Guid.NewGuid().ToString("D") } };
            var identity = Service().Get("bob", "other", _client.Record.Message.MessageId).Identity;
            var result = await Service().QueryAsync("bob", "ip", "other", identity.MessageId,
                new(Guid.NewGuid().ToString("D"), identity.Digest, "Other peer incident"), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Observed", result.State);
            Assert.False(first.IsCompleted);
        }
        finally { release.SetResult(); await first; }
    }

    [Fact]
    public async Task EndpointsRequireSystemPermissionAndEnforceTargetScope()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Service());
        await using var app = builder.Build();
        app.MapNodeReconciliation(12345);
        using var source = new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);
        var endpoints = source.Endpoints.OfType<RouteEndpoint>().ToArray();
        Assert.Equal(4, endpoints.Length);
        foreach (var endpoint in endpoints)
        {
            Assert.Contains("*:12345", endpoint.Metadata.GetMetadata<IHostMetadata>()!.Hosts);
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<ManagementPermissionRequirement>(), r => r.Permission == ManagementPermissions.ManageSystem);
            Assert.Null(endpoint.Metadata.GetMetadata<ManagementStepUpExemption>());
            var context = new DefaultHttpContext();
            context.SetEndpoint(endpoint);
            Assert.False(ManagementAuthorization.IsAllowed(context, [ManagementPermissions.Read]));
            Assert.True(ManagementAuthorization.IsAllowed(context, [ManagementPermissions.ManageSystem]));
        }
        var detail = endpoints.Single(e => e.RoutePattern.RawText!.EndsWith("/{messageId}"));
        var denied = new DefaultHttpContext { RequestServices = app.Services };
        denied.Items["Username"] = "bob";
        denied.Request.RouteValues["nodeId"] = "edge";
        denied.Request.RouteValues["messageId"] = _client.Record.Message.MessageId;
        denied.Response.Body = new MemoryStream();
        await detail.RequestDelegate!(denied);
        Assert.Equal(403, denied.Response.StatusCode);
        Assert.Equal(0, _client.Queries);
    }

    [Fact]
    public async Task AssessmentEndpointRecordsAuthenticatedActorAndCannotAcceptAnAck()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Service());
        await using var app = builder.Build();
        app.MapNodeReconciliation(12345);
        using var source = new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);
        var endpoint = source.Endpoints.OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText!.EndsWith("/assessment"));
        var request = Request("OperatorBelievesApplied");
        var json = JsonSerializer.SerializeToNode(request, Json)!;
        json["actor"] = "forged";
        json["observedNodeResult"] = new JsonObject { ["ack"] = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.SetEndpoint(endpoint);
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
        context.Items["Username"] = "alice";
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Request.RouteValues["nodeId"] = "edge";
        context.Request.RouteValues["messageId"] = _client.Record.Message.MessageId;
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.True(context.Response.StatusCode == 200, System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        var action = Assert.Single(Service().Get("alice", "edge", _client.Record.Message.MessageId).Actions);
        Assert.Equal("alice", action.Actor);
        Assert.Null(action.ObservedNodeResult);
        Assert.Equal(0, _client.Queries + _client.Commands);
    }

    private static NodeAcknowledgement Ack() => new(true, "edge", "d", "commit", "revision");
    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
    private sealed class Client : IDistributionNodeClient
    {
        public NodeOutboxRecord Record = new(new(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "d", "configuration.commit",
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-5), JsonSerializer.SerializeToElement(new { deploymentId = "d", secret = "SECRET_CONFIG" })), "https://edge", 3, "Unknown");
        public int Queries;
        public int Commands;
        public Func<NodeOutboxRecord, CancellationToken, Task<NodeAcknowledgement>> Query = (_, _) => Task.FromResult(Ack());
        public IReadOnlyList<NodeOutboxRecord> ReadOutbox() => [Record];
        public Task<NodeAcknowledgement> QueryRecordedOutcomeAsync(NodeOutboxRecord record, CancellationToken token) { Queries++; return Query(record, token); }
        public Task<NodeAcknowledgement> PrepareAsync(string id, PrepareConfigurationRequest request, CancellationToken token) { Commands++; throw new Exception("must not prepare"); }
        public Task<NodeAcknowledgement> CommandAsync(string id, string operation, DistributionCommandRequest request, CancellationToken token) { Commands++; throw new Exception("must not command"); }
        public Task<DistributionNodeStatus> GetStatusAsync(string id, CancellationToken token) => throw new NotImplementedException();
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string id, CancellationToken token) => throw new NotImplementedException();
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public List<NodeMessage> Messages { get; } = [];
        public List<string> Nonces { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.EndsWith("configuration.result", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.Contains("X-LyWaf-Signature"));
            Messages.Add((await request.Content!.ReadFromJsonAsync<NodeMessage>(Json, token))!);
            Nonces.Add(request.Headers.GetValues("X-LyWaf-Nonce").Single());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Ack()) };
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
