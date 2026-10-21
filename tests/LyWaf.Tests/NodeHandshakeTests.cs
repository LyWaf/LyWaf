using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeHandshakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "handshake-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly StaticOptionsMonitor<NodeCommunicationOptions> _options;
    private readonly NodeRegistrationObserver _observer;
    private readonly Client _client = new();

    public NodeHandshakeTests()
    {
        _options = new(new() { Enabled = true, ClusterId = "cluster", NodeId = "controller", Role = "Controller",
            StateDirectory = _root, Peers = new() { ["edge"] = new() { Role = "Node",
                AllowedOperations = ["node.handshake", "node.status", "configuration.result"] } } });
        _observer = new(_options, _clock);
    }

    private NodeHandshake Handshake(string? boot = null) => new("cluster", "edge", boot ?? Guid.NewGuid().ToString("N"),
        "Node", _clock.Now.AddSeconds(-10), "1.0.0", [2], new("revision", new string('A', 64), "revision"),
        new(NodeHandshakeMetadataService.ComputeDigest([]), []));

    private NodeMessage Envelope(NodeHandshake handshake) => new(2, "cluster", "edge", "controller", Guid.NewGuid().ToString("D"),
        "handshake", "node.handshake", _clock.Now, _clock.Now.AddMinutes(1), JsonSerializer.SerializeToElement(handshake, NodeMessageAuthentication.JsonOptions));
    private NodeRegistrationObservation Observe(NodeHandshake handshake) => _observer.ObserveAuthenticated(Envelope(handshake));
    private Task<NodeHandshakeRecoveryResult> Reconcile(NodeRegistrationObservation observation, CancellationToken token = default)
        => new NodeHandshakeRecoveryService(_observer, _client, _options, new Probe(observation.Handshake))
            .ReconcileAsync("edge", observation.ObservationId, token);

    [Fact]
    public void DurableSessionEndAllowsNewBootButCannotResurrectRetiredBoot()
    {
        var original = Handshake();
        Observe(original);
        var ending = Observe(original with { Ending = true });
        Assert.False(_observer.CanStartTasks("edge", ending.ObservationId));
        Assert.False(_observer.CompleteReconciliation("edge", ending.ObservationId, new string('A', 64)));
        Assert.Throws<InvalidDataException>(() => Observe(original));
        var next = Handshake();
        var reopened = new NodeRegistrationObserver(_options, _clock).ObserveAuthenticated(Envelope(next));
        Assert.Equal("NeedsReconciliation", reopened.State);
        Assert.Contains(original.BootId, reopened.RetiredBootIds);
        Assert.Throws<InvalidDataException>(() => Observe(original with { Ending = true }));
    }

    [Fact]
    public void UnannouncedBootConflictCannotBeClearedBySessionEnd()
    {
        var original = Handshake(); Observe(original);
        Assert.Equal("IdentityConflict", Observe(Handshake()).State);
        Assert.Equal("IdentityConflict", Observe(original with { Ending = true }).State);
        Assert.Equal("IdentityConflict", _observer.Get("edge")!.State);
    }

    [Fact]
    public async Task NewObservationCannotStartTasksUntilReadOnlyReconciliationSucceeds()
    {
        var observation = Observe(Handshake());
        _client.Records = [Record()];
        Assert.False(_observer.CanStartTasks("edge", observation.ObservationId));
        var result = await Reconcile(observation);
        Assert.True(result.Ready);
        Assert.Equal(1, result.ConfirmedCommands);
        Assert.Equal(1, _client.Queries);
        Assert.True(_observer.CanStartTasks("edge", observation.ObservationId));
        Assert.Null(_client.Records[0].Result); // Transport evidence did not fabricate an outbox ACK.
        Assert.Equal(observation.ObservationId, Observe(observation.Handshake).ObservationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedReconnectRequiresMatchingDurablePrepareAndFreshResultQuery(bool completed)
    {
        var observation = Observe(Handshake());
        var record = Record();
        _client.Records = [record with
        {
            Message = record.Message with { Type = "configuration.prepare" },
            Result = completed ? new(true, "edge", "deployment", "prepare", "revision") : null
        }];
        _client.Failure = "prepared";
        var result = await Reconcile(observation);
        Assert.True(result.Ready);
        Assert.True(_client.Queries >= 1);
        Assert.Equal(1, result.ConfirmedCommands);
        Assert.True(_observer.CanStartTasks("edge", observation.ObservationId));

        _observer.MarkDisconnected("edge");
        var reconnected = Observe(observation.Handshake);
        _client.Records = [];
        Assert.False((await Reconcile(reconnected)).Ready);
        Assert.False(_observer.CanStartTasks("edge", reconnected.ObservationId));
    }

    [Fact]
    public async Task PreparedEvidenceChangingDuringQueryCannotOpenGate()
    {
        var observation = Observe(Handshake());
        var record = Record();
        _client.Records = [record with
        {
            Message = record.Message with { Type = "configuration.prepare" },
            Result = new(true, "edge", "deployment", "prepare", "revision")
        }];
        _client.Failure = "prepared";
        _client.OnQuery = () => _client.Records = [];
        var result = await Reconcile(observation);
        Assert.False(result.Ready);
        Assert.Equal("node_handshake_outbox_changed", result.Code);
        Assert.False(_observer.CanStartTasks("edge", observation.ObservationId));
    }

    [Fact]
    public async Task DisconnectExpiryAndControllerRestartCloseTaskGate()
    {
        var observation = Observe(Handshake());
        Assert.True((await Reconcile(observation)).Ready);
        _observer.MarkDisconnected("edge");
        Assert.False(_observer.CanStartTasks("edge", observation.ObservationId));
        var reconnected = Observe(observation.Handshake);
        Assert.NotEqual(observation.ObservationId, reconnected.ObservationId);
        Assert.True((await Reconcile(reconnected)).Ready);
        _clock.Now = _clock.Now.AddSeconds(61);
        Assert.False(_observer.CanStartTasks("edge", reconnected.ObservationId));
        var afterExpiry = Observe(observation.Handshake);
        Assert.NotEqual(reconnected.ObservationId, afterExpiry.ObservationId);
        Assert.True((await Reconcile(afterExpiry)).Ready);
        var restarted = new NodeRegistrationObserver(_options, _clock);
        Assert.False(restarted.CanStartTasks("edge", afterExpiry.ObservationId));
        Assert.Equal("NeedsReconciliation", restarted.ObserveAuthenticated(Envelope(observation.Handshake)).State);
    }

    [Fact]
    public async Task DisconnectWriteFailureFencesOldReadyUntilNewHandshakeAndReconciliationAreDurable()
    {
        var old = Observe(Handshake());
        Assert.True((await Reconcile(old)).Ready);
        _observer.BeforePersist = () => throw new IOException("Injected registration write failure");
        Assert.Throws<IOException>(() => _observer.MarkDisconnected("edge"));
        Assert.Equal("Ready", _observer.Get("edge")!.State);
        Assert.False(_observer.CanStartTasks("edge", old.ObservationId));
        Assert.Throws<IOException>(() => Observe(old.Handshake));
        _observer.BeforePersist = null;
        Assert.False(_observer.CompleteReconciliation("edge", old.ObservationId, "old-result"));
        Assert.False((await Reconcile(old)).Ready);
        var fresh = Observe(old.Handshake);
        Assert.NotEqual(old.ObservationId, fresh.ObservationId);
        Assert.False(_observer.CanStartTasks("edge", fresh.ObservationId));
        _observer.BeforePersist = () => throw new IOException("Injected ready commit failure");
        Assert.Throws<IOException>(() => _observer.CompleteReconciliation("edge", fresh.ObservationId, "fresh-result"));
        Assert.False(_observer.CanStartTasks("edge", fresh.ObservationId));
        _observer.BeforePersist = null;
        Assert.True((await Reconcile(fresh)).Ready);
        Assert.True(_observer.CanStartTasks("edge", fresh.ObservationId));
        Assert.False(_observer.CanStartTasks("edge", old.ObservationId));
    }

    [Fact]
    public async Task ConcurrentLiveBootsPersistIdentityConflictAcrossRestart()
    {
        var first = Observe(Handshake());
        Assert.True((await Reconcile(first)).Ready);
        var second = Observe(Handshake());
        Assert.Equal("IdentityConflict", second.State);
        Assert.False(_observer.CanStartTasks("edge", first.ObservationId));
        _clock.Now = _clock.Now.AddMinutes(2);
        var restarted = new NodeRegistrationObserver(_options, _clock);
        Assert.Equal("IdentityConflict", restarted.ObserveAuthenticated(Envelope(first.Handshake)).State);
        Assert.False((await Reconcile(second)).Ready);
    }

    [Fact]
    public void OfflineNewBootRequiresReconciliationAndRetiredBootCannotReturn()
    {
        var first = Observe(Handshake());
        _clock.Now = _clock.Now.AddSeconds(61);
        var second = Observe(Handshake());
        Assert.Equal("NeedsReconciliation", second.State);
        Assert.Contains(first.Handshake.BootId, second.RetiredBootIds);
        _clock.Now = _clock.Now.AddSeconds(61);
        Assert.Throws<InvalidDataException>(() => Observe(first.Handshake));
        Assert.Equal(second.ObservationId, _observer.Get("edge")!.ObservationId);
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Draining")]
    public async Task HandshakeDoesNotApproveOrOpenTasksForRestrictedPeer(string state)
    {
        _options.CurrentValue.Peers["edge"].AdmissionState = state;
        var observation = Observe(Handshake());
        Assert.False((await Reconcile(observation)).Ready);
        Assert.False(_observer.CanStartTasks("edge", observation.ObservationId));
        Assert.Equal(state, _options.CurrentValue.Peers["edge"].AdmissionState);
        Assert.Equal(0, _client.StatusCalls);
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("cluster")]
    [InlineData("role")]
    [InlineData("digest")]
    [InlineData("missing")]
    [InlineData("protocol")]
    public void InvalidHandshakeCannotRegister(string change)
    {
        var handshake = Handshake();
        handshake = change switch
        {
            "sender" => handshake with { NodeId = "other" }, "cluster" => handshake with { ClusterId = "other" },
            "role" => handshake with { Role = "Controller" }, "digest" => handshake with { Plugins = new("wrong", []) },
            "missing" => handshake with { Configuration = null }, _ => handshake with { ProtocolVersions = [1] }
        };
        Assert.Throws<InvalidDataException>(() => Observe(handshake));
        Assert.Null(_observer.Get("edge"));
    }

    [Fact]
    public void RevokedOrUnknownPeerIsRejected()
    {
        _options.CurrentValue.Peers["edge"].AdmissionState = "Revoked";
        Assert.Throws<UnauthorizedAccessException>(() => Observe(Handshake()));
        _options.CurrentValue.Peers.Clear();
        Assert.Throws<UnauthorizedAccessException>(() => Observe(Handshake()));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("wrong-node")]
    [InlineData("revision")]
    [InlineData("prepared")]
    [InlineData("network")]
    [InlineData("outbox-changed")]
    public async Task FailedOrRacingReconciliationNeverOpensGate(string failure)
    {
        var observation = Observe(Handshake());
        _client.Records = [Record()];
        _client.Failure = failure;
        Assert.False((await Reconcile(observation)).Ready);
        Assert.False(_observer.CanStartTasks("edge", observation.ObservationId));
    }

    [Fact]
    public async Task ExpiredCandidateCanReconcileWithoutExecutingOrReusingIt()
    {
        var observed = Observe(Handshake());
        _client.Failure = "expired";
        var result = await Reconcile(observed);
        Assert.True(result.Ready);
        Assert.Equal(0, _client.Queries);
        Assert.Equal(1, _client.StatusCalls);
        Assert.True(_observer.CanStartTasks("edge", observed.ObservationId));
    }

    [Fact]
    public async Task ChangedMetadataOrDisconnectDuringQueryInvalidatesOldCompletion()
    {
        var first = Observe(Handshake());
        _client.OnStatus = () => Observe(first.Handshake with { ApplicationVersion = "2.0.0" });
        Assert.False((await Reconcile(first)).Ready);
        var second = _observer.Get("edge")!;
        _client.OnStatus = () => _observer.MarkDisconnected("edge");
        Assert.False((await Reconcile(second)).Ready);
    }

    [Fact]
    public async Task CancelledRecheckClosesPreviouslyReadyGate()
    {
        var first = Observe(Handshake());
        Assert.True((await Reconcile(first)).Ready);
        using var cancelled = new CancellationTokenSource();
        _client.OnStatus = cancelled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reconcile(first, cancelled.Token));
        Assert.False(_observer.CanStartTasks("edge", first.ObservationId));
    }

    [Fact]
    public async Task FreshHandshakeWithDifferentBootCannotFinishOldReconciliation()
    {
        var first = Observe(Handshake());
        var service = new NodeHandshakeRecoveryService(_observer, _client, _options,
            new Probe(first.Handshake with { BootId = Guid.NewGuid().ToString("N") }));
        var result = await service.ReconcileAsync("edge", first.ObservationId, default);
        Assert.False(result.Ready);
        Assert.Equal("node_handshake_metadata_changed", result.Code);
        Assert.False(_observer.CanStartTasks("edge", first.ObservationId));
    }

    [Fact]
    public void CorruptRegistrationCannotBeConfirmedOrTreatedAsReady()
    {
        var first = Observe(Handshake());
        var conflict = Observe(Handshake());
        File.WriteAllText(Assert.Single(Directory.GetFiles(Path.Combine(_root, "registrations"), "*.json")), "corrupt");
        Assert.Throws<JsonException>(() => _observer.CanStartTasks("edge", first.ObservationId));
        Assert.Throws<JsonException>(() => new NodeIdentityConflictResolutionService(_observer)
            .Confirm(Admin(), "edge", Resolution(conflict, conflict.ConflictingBootId!, first.Handshake.BootId)));
    }

    [Fact]
    public async Task AdminCanConfirmRollingRestartButMustHandshakeAndReconcileBeforeTasks()
    {
        var first = Observe(Handshake());
        Assert.True((await Reconcile(first)).Ready);
        var replacement = Handshake();
        var conflict = Observe(replacement);
        var request = Resolution(conflict, replacement.BootId, first.Handshake.BootId);
        var service = new NodeIdentityConflictResolutionService(_observer);
        var accepted = service.Confirm(Admin(), "edge", request);
        Assert.Equal("NeedsReconciliation", accepted.State);
        Assert.False(_observer.CanStartTasks("edge", accepted.ObservationId));
        Assert.False((await Reconcile(accepted)).Ready);
        var audit = Assert.Single(accepted.IdentityResolutions!);
        Assert.Equal("operator", audit.Actor);
        Assert.Equal(request.Reason, audit.Reason);
        Assert.Equal(first.Handshake.BootId, audit.RetiredBootId);
        Assert.Equal(replacement.BootId, audit.AcceptedBootId);
        Assert.Equal(accepted.ObservationId, service.Confirm(Admin(), "edge", request).ObservationId);
        Assert.Single(_observer.Get("edge")!.IdentityResolutions!);
        var live = Observe(replacement);
        Assert.True((await Reconcile(live)).Ready);
        Assert.Throws<InvalidDataException>(() => Observe(first.Handshake));
        Assert.True(_observer.CanStartTasks("edge", live.ObservationId));
        var restarted = new NodeRegistrationObserver(_options, _clock);
        Assert.Single(restarted.Get("edge")!.IdentityResolutions!);
        Assert.Throws<InvalidDataException>(() => restarted.ObserveAuthenticated(Envelope(first.Handshake)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("write")]
    [InlineData("read")]
    public void ConflictResolutionRequiresManagementAdministrator(string? permission)
    {
        var first = Observe(Handshake());
        var conflict = Observe(Handshake());
        var context = Admin();
        context.Items["ManagementScopes"] = permission is null ? Array.Empty<string>() : new[] { permission };
        Assert.Throws<UnauthorizedAccessException>(() => new NodeIdentityConflictResolutionService(_observer)
            .Confirm(context, "edge", Resolution(conflict, conflict.ConflictingBootId!, first.Handshake.BootId)));
        Assert.Equal("IdentityConflict", _observer.Get("edge")!.State);
        Assert.Null(_observer.Get("edge")!.IdentityResolutions);
    }

    [Fact]
    public void StaleOrUnobservedBootCannotBeConfirmedAndRequestIdCannotBeRebound()
    {
        var first = Observe(Handshake());
        var conflict = Observe(Handshake());
        var service = new NodeIdentityConflictResolutionService(_observer);
        var request = Resolution(conflict, conflict.ConflictingBootId!, first.Handshake.BootId);
        Assert.Throws<InvalidOperationException>(() => service.Confirm(Admin(), "edge", request with { ObservationId = Guid.NewGuid().ToString("N") }));
        Assert.Throws<InvalidOperationException>(() => service.Confirm(Admin(), "edge", request with { AcceptedBootId = Guid.NewGuid().ToString("N") }));
        service.Confirm(Admin(), "edge", request);
        Assert.Throws<InvalidOperationException>(() => service.Confirm(Admin(), "edge", request with { Reason = "different" }));
        Assert.Single(_observer.Get("edge")!.IdentityResolutions!);
    }

    [Fact]
    public async Task ManagementApiExposesAuditWithoutClaimingReadyAndBoundsBodies()
    {
        var first = Observe(Handshake());
        var conflict = Observe(Handshake());
        var service = new NodeIdentityConflictResolutionService(_observer);
        var request = Resolution(conflict, conflict.ConflictingBootId!, first.Handshake.BootId);
        var context = Admin();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, NodeMessageAuthentication.JsonOptions));
        var result = await service.HandleAsync(context, "edge");
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var body = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
        Assert.False(body.GetProperty("tasksAllowed").GetBoolean());
        var view = service.Read(Admin(), "edge");
        Assert.Equal(200, ((IStatusCodeHttpResult)view).StatusCode);
        Assert.Single(_observer.Get("edge")!.IdentityResolutions!);
        context.Request.Body = new MemoryStream(new byte[4097]);
        Assert.Equal(413, ((IStatusCodeHttpResult)await service.HandleAsync(context, "edge")).StatusCode);
        context.Items.Remove("ManagementScopes");
        Assert.Equal(403, ((IStatusCodeHttpResult)await service.HandleAsync(context, "edge")).StatusCode);
        Assert.Equal(403, ((IStatusCodeHttpResult)service.Read(context, "edge")).StatusCode);
    }

    [Fact]
    public async Task CorruptJournalIsUnavailableNotInvalidRequestAndNeverRecreated()
    {
        var first = Observe(Handshake());
        var conflict = Observe(Handshake());
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "registrations"), "*.json"));
        File.WriteAllText(path, "corrupt");
        var context = Admin();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
            Resolution(conflict, conflict.ConflictingBootId!, first.Handshake.BootId), NodeMessageAuthentication.JsonOptions));
        var service = new NodeIdentityConflictResolutionService(_observer);
        Assert.Equal(503, ((IStatusCodeHttpResult)await service.HandleAsync(context, "edge")).StatusCode);
        Assert.Equal(503, ((IStatusCodeHttpResult)service.Read(Admin(), "edge")).StatusCode);
        Assert.Equal("corrupt", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(400, "node_identity_resolution_invalid")]
    [InlineData(403, "node_identity_admin_required")]
    [InlineData(404, "node_identity_observation_missing")]
    [InlineData(413, "node_message_too_large")]
    [InlineData(415, "node_identity_content_type_required")]
    [InlineData(409, "node_identity_resolution_conflict")]
    [InlineData(503, "node_identity_resolution_unavailable")]
    public async Task ManagementErrorsCarryUniformLocalizedTraceContract(int status, string code)
    {
        var context = Admin();
        context.TraceIdentifier = "identity-test-trace";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("{"u8.ToArray());
        var service = new NodeIdentityConflictResolutionService(_observer);
        if (status == 403) context.Items.Remove("ManagementScopes");
        if (status == 413) context.Request.ContentLength = 4097;
        if (status == 415) context.Request.ContentType = "text/plain";
        if (status is 409 or 503)
        {
            var first = Observe(Handshake());
            var conflict = Observe(Handshake());
            var request = Resolution(conflict, conflict.ConflictingBootId!, first.Handshake.BootId);
            if (status == 409) request = request with { ObservationId = Guid.NewGuid().ToString("N") };
            else File.WriteAllText(Assert.Single(Directory.GetFiles(Path.Combine(_root, "registrations"), "*.json")), "corrupt");
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(request, NodeMessageAuthentication.JsonOptions));
        }
        var result = status == 404 ? service.Read(context, "edge") : await service.HandleAsync(context, "edge");
        Assert.Equal(status, ((IStatusCodeHttpResult)result).StatusCode);
        var error = Assert.IsType<NodeErrorResponse>(((IValueHttpResult)result).Value);
        Assert.False(error.Success);
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal("node.errors." + code, error.MessageKey);
        Assert.Equal(context.TraceIdentifier, error.TraceId);
    }

    private static NodeIdentityConflictRequest Resolution(NodeRegistrationObservation conflict, string accepted, string retired)
        => new(Guid.NewGuid().ToString("D"), conflict.ObservationId, accepted, retired, "Verified rolling restart and old process stopped");
    private static DefaultHttpContext Admin()
    {
        var context = new DefaultHttpContext();
        context.Items["ManagementScopes"] = new[] { "admin" };
        context.Items["Username"] = "operator";
        return context;
    }

    private NodeOutboxRecord Record() => new(new(2, "cluster", "controller", "edge", Guid.NewGuid().ToString("D"),
        "deployment", "configuration.commit", _clock.Now, _clock.Now.AddMinutes(1), JsonSerializer.SerializeToElement(new { deploymentId = "deployment" })),
        "https://edge", Attempts: 1, State: "Unknown");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Probe(NodeHandshake handshake) : INodeHandshakePeerProbe
    {
        public Task<NodeHandshake> CaptureAsync(string peer, CancellationToken token) => Task.FromResult(handshake);
    }

    private sealed class Client : IDistributionNodeClient
    {
        public NodeOutboxRecord[] Records = [];
        public string? Failure;
        public int Queries, StatusCalls;
        public Action? OnStatus;
        public Action? OnQuery;
        public IReadOnlyList<NodeOutboxRecord> ReadOutbox() => Records;
        public Task<NodeAcknowledgement> QueryRecordedOutcomeAsync(NodeOutboxRecord record, CancellationToken token)
        {
            Queries++;
            OnQuery?.Invoke();
            if (Failure == "network") throw new HttpRequestException();
            return Task.FromResult(new NodeAcknowledgement(Failure != "negative", Failure == "wrong-node" ? "other" : "edge",
                record.Message.CorrelationId, record.Message.Type["configuration.".Length..], "revision"));
        }
        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken token)
        {
            StatusCalls++;
            OnStatus?.Invoke();
            if (Failure == "outbox-changed") Records = [];
            return Task.FromResult(new DistributionNodeStatus("edge", "Node", "1.0.0", "deployment",
                Failure == "revision" ? "different" : "revision", Failure == "prepared" ? "Prepared" : Failure == "expired" ? "Expired" : "Applied", DateTimeOffset.UtcNow));
        }
        public Task<NodeAcknowledgement> PrepareAsync(string id, PrepareConfigurationRequest request, CancellationToken token) => throw new Xunit.Sdk.XunitException("Must never execute prepare");
        public Task<NodeAcknowledgement> CommandAsync(string id, string op, DistributionCommandRequest request, CancellationToken token) => throw new Xunit.Sdk.XunitException("Must never execute commands");
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
