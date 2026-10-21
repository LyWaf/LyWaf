using System.Collections.Concurrent;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.Telemetry;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

internal sealed class RuntimeStateSyncTransport(HttpClient client,
    IOptionsMonitor<RuntimeStateOptions> options, IOptionsMonitor<NodeCommunicationOptions>? communication, RuntimeStateStore? store = null,
    NodeCommunicationTelemetry? telemetry = null, NodeSocketConnections? sockets = null)
{
    private readonly RuntimeStateSyncSender _sender = new(options);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private sealed record Download(string PendingDigest, RuntimeStateBootstrapManifest Manifest);
    internal Action<string>? BootstrapCacheCleanupFaultPoint { get; set; }

    public async Task SendAsync(string peerId, RuntimeStateMutation? mutation, CancellationToken token,
        System.Threading.Channels.ChannelReader<RuntimeStateMutation>? queue = null)
    {
        var gate = _gates.GetOrAdd(peerId, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            var current = options.CurrentValue;
            var config = communication?.CurrentValue;
            if (!current.Enabled || !current.Peers.TryGetValue(peerId, out var peer) || !peer.Enabled || !peer.DurableSyncEnabled) return;
            if (peer.ProtocolVersion != 2 || config is not { Enabled: true } || config.NodeId != current.NodeId ||
                !config.Peers.TryGetValue(peerId, out var identity) || !NodeAdmissionPolicy.Allows(identity, RuntimeStateSyncProtocol.Operation) ||
                !identity.Keys.TryGetValue(identity.ActiveKeyId, out var secret) || System.Text.Encoding.UTF8.GetByteCount(secret) < 32)
                throw new InvalidOperationException("Runtime sync identity is not configured");
            if (!Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
                (config.RequireHttps && uri.Scheme != "https")) throw new InvalidOperationException("Invalid runtime sync endpoint");
            CleanupCompletedDownload();
            if (_sender.ReadPending(config.ClusterId, config.NodeId, peerId) is { } pending)
            {
                if (store is not null && pending.Mutations.Any(m => !store.IsCurrentForSync(m)))
                {
                    var probe = pending with { Mutations = [], Bootstrap = new("probe", Guid.NewGuid().ToString("N"), "") };
                    probe = probe with { Digest = RuntimeStateSyncProtocol.ComputeDigest(probe) };
                    var status = await Exchange(probe);
                    if (status.Status != "SnapshotRequired") throw new InvalidDataException("Invalid bootstrap probe response");
                    await Bootstrap(pending, status.Epoch);
                }
                else await Deliver(pending);
            }
            if (mutation is not null && (store is null || store.IsCurrentForSync(mutation)))
            {
                var mutations = RuntimeStateSyncBatchPacking.Take(config, peerId, mutation, queue,
                    next => (store is null || store.IsCurrentForSync(next))
                        && RuntimeStateReplicationAuthorization.AuthorizeSend(options.CurrentValue, peerId, next) is null);
                await Deliver(_sender.Prepare(config.ClusterId, config.NodeId, peerId, mutations));
            }

            void CheckAdmission()
            {
                var latest = communication?.CurrentValue;
                if (latest is not { Enabled: true } || latest.NodeId != config.NodeId || latest.ClusterId != config.ClusterId
                    || !latest.Peers.TryGetValue(peerId, out var admitted)
                    || !NodeAdmissionPolicy.Allows(admitted, RuntimeStateSyncProtocol.Operation))
                    throw new InvalidOperationException("Runtime sync peer admission no longer permits replication");
            }

            void CleanupCompletedDownload()
            {
                var files = new RuntimeStateSyncFiles(current.StateDirectory, current);
                var path = files.PathFor("download-offer", config.ClusterId, config.NodeId, peerId);
                var download = files.Read<Download>(path);
                if (download is null) return;
                if (download.Manifest is null) throw new InvalidDataException("Missing download manifest");
                download.Manifest.Validate();
                var header = download.Manifest.Header;
                if (header.ClusterId != config.ClusterId || header.RecipientNodeId != config.NodeId
                    || header.AuthorityNodeId != peerId) throw new InvalidDataException("Download cache stream mismatch");
                if (!_sender.HasCompletedBootstrap(download.Manifest)) return;
                BootstrapCacheCleanupFaultPoint?.Invoke("Rebased");
                new RuntimeStateBootstrapPages(current.StateDirectory, "download-page", current)
                    .DeleteCompletedDownload(download.Manifest, () => BootstrapCacheCleanupFaultPoint?.Invoke("PageDeleted"));
                // Keep the manifest until every page is gone: interrupted cleanup resumes without network.
                File.Delete(path);
            }

            async Task<RuntimeStateSyncAck> Exchange(RuntimeStateSyncBatch batch)
            {
                CheckAdmission();
                // Recheck disk retries against the current policy, including forwarded component origins.
                if (batch.Mutations.Any(m => RuntimeStateReplicationAuthorization.AuthorizeSend(options.CurrentValue, peerId, m) is not null))
                    throw new InvalidOperationException("Runtime sync retry authorization revoked");
                var now = DateTimeOffset.UtcNow;
                var message = new NodeMessage(2, config.ClusterId, config.NodeId, peerId, Guid.NewGuid().ToString("D"),
                    batch.Epoch, RuntimeStateSyncProtocol.Operation, now, now.AddMinutes(5),
                    JsonSerializer.SerializeToElement(batch, RuntimeStateSyncProtocol.Json));
                var body = JsonSerializer.SerializeToUtf8Bytes(message, RuntimeStateSyncProtocol.Json);
                if (body.Length > config.MaxMessageBytes) throw new InvalidOperationException("Runtime sync message exceeds configured limit");
                using var request = new HttpRequestMessage(HttpMethod.Post, peer.Endpoint.TrimEnd('/') + NodeMessageAuthentication.Prefix + message.Type)
                { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new("application/json");
                NodeMessageAuthentication.Sign(request, message, identity.ActiveKeyId, secret, body);
                CheckAdmission();
                using var response = await NodeControlTransport.SendAsync(config, sockets, peerId, client, request, token);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
                    throw await NodeRemoteError.ReadAsync(response, token);
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var bounded = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                {
                    if (bounded.Length + count > (batch.Bootstrap is null ? 16384 : RuntimeStateSyncProtocol.MaximumBatchBytes + 16384))
                        throw new InvalidDataException("Oversized runtime sync ACK");
                    bounded.Write(buffer, 0, count);
                }
                var responseBytes = bounded.ToArray();
                if (!response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(responseBytes);
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("errorCode", out _))
                    {
                        using var rejected = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(responseBytes) };
                        rejected.Headers.RetryAfter = response.Headers.RetryAfter;
                        throw await NodeRemoteError.ReadAsync(rejected, token);
                    }
                }
                var ack = JsonSerializer.Deserialize<RuntimeStateSyncAck>(responseBytes, RuntimeStateSyncProtocol.Json)
                    ?? throw new InvalidDataException("Missing runtime sync ACK");
                if (ack.ClusterId != batch.ClusterId || ack.SenderNodeId != batch.SenderNodeId || ack.ReceiverNodeId != batch.ReceiverNodeId)
                    throw new InvalidDataException("Runtime sync reply identity mismatch");
                if (!response.IsSuccessStatusCode && ack.Status is not ("Gap" or "SnapshotRequired"))
                    response.EnsureSuccessStatusCode();
                return ack;
            }

            async Task Bootstrap(RuntimeStateSyncBatch pending, string epoch)
            {
                if (store is null || !options.CurrentValue.Peers[peerId].BootstrapAuthority)
                    throw new UnauthorizedAccessException("No trusted bootstrap authority configured");
                var scope = RuntimeStateBootstrapScope.From(options.CurrentValue, options.CurrentValue.Peers[peerId].Receive);
                var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory, options.CurrentValue);
                var downloadPath = files.PathFor("download-offer", config.ClusterId, config.NodeId, peerId);
                var download = files.Read<Download>(downloadPath);
                var requestId = download is not null && download.PendingDigest == pending.Digest
                    && DateTimeOffset.UtcNow - download.Manifest.Header.CapturedAt <= TimeSpan.FromMinutes(5)
                    ? download.Manifest.Header.RequestId
                    : RuntimeStateBootstrapImage.Hash(new { pending.Digest, Window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 240 })[..32].ToLowerInvariant();
                var fetch = new RuntimeStateSyncBatch(pending.ClusterId, pending.SenderNodeId, pending.ReceiverNodeId,
                    epoch, pending.Sequence, [], "", new("fetch", requestId, scope.Digest()));
                fetch = fetch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(fetch) };
                var offered = await Exchange(fetch);
                var manifest = offered.Manifest;
                manifest?.Validate();
                var image = manifest?.Header ?? offered.Snapshot ?? throw new InvalidDataException("Missing bootstrap image");
                if (manifest is null) image.Validate();
                if (offered.Status != "SnapshotOffered" || offered.Digest != fetch.Digest || offered.Epoch != fetch.Epoch
                    || image.RequestId != requestId || (image.OldEpoch != epoch && image.NewEpoch != epoch) || image.ClusterId != config.ClusterId
                    || image.AuthorityNodeId != peerId || image.RecipientNodeId != config.NodeId || image.Scope.Digest() != scope.Digest()
                    || DateTimeOffset.UtcNow - image.CapturedAt > TimeSpan.FromMinutes(5) || image.CapturedAt > DateTimeOffset.UtcNow.AddSeconds(60))
                    throw new InvalidDataException("Bootstrap image identity, scope or lifetime mismatch");
                if (manifest is not null)
                {
                    if (download is not null && download.PendingDigest == pending.Digest
                        && download.Manifest.Header.RequestId == requestId
                        && RuntimeStateBootstrapImage.Hash(download.Manifest) != RuntimeStateBootstrapImage.Hash(manifest))
                        throw new InvalidDataException("Bootstrap manifest changed during download");
                    files.Save(downloadPath, new Download(pending.Digest, manifest), token);
                    var pages = new RuntimeStateBootstrapPages(options.CurrentValue.StateDirectory, "download-page", options.CurrentValue);
                    pages.PruneOtherSnapshots(manifest);
                    for (var index = 0; index < manifest.PageDigests.Length; index++)
                    {
                        CheckAdmission();
                        if (pages.ReadDownloadCache(manifest, index) is not null) continue;
                        var pageRequest = fetch with { Bootstrap = new("page", requestId, scope.Digest(), image.Digest, index) };
                        pageRequest = pageRequest with { Digest = RuntimeStateSyncProtocol.ComputeDigest(pageRequest) };
                        var received = await Exchange(pageRequest);
                        if (received.Status != "SnapshotPage" || received.Epoch != pageRequest.Epoch
                            || received.Digest != pageRequest.Digest || received.Page is null || received.Page.Index != index)
                            throw new InvalidDataException("Invalid bootstrap page response");
                        pages.Save(manifest, received.Page, token);
                    }
                    image = pages.Assemble(manifest);
                }
                CheckAdmission();
                await store.InstallBootstrapAsync(image, token);
                var commit = fetch with { Epoch = image.NewEpoch, Bootstrap = new("commit", requestId, scope.Digest(), image.Digest) };
                commit = commit with { Digest = RuntimeStateSyncProtocol.ComputeDigest(commit) };
                var completed = await Exchange(commit);
                if (completed.Status != "Bootstrapped" || completed.Epoch != image.NewEpoch || completed.Sequence != 0 || completed.Digest != image.Digest)
                    throw new InvalidDataException("Bootstrap admission was not confirmed");
                _sender.Rebase(pending, image);
                CleanupCompletedDownload();
            }

            async Task Deliver(RuntimeStateSyncBatch batch, bool confirm = true, bool repairGap = true)
            {
                var ack = await Exchange(batch);
                if (ack.Status == "SnapshotRequired" && confirm)
                {
                    await Bootstrap(batch, ack.Epoch);
                    return;
                }
                if (ack.Status == "Gap" && repairGap && confirm)
                {
                    if (ack.ClusterId != batch.ClusterId || ack.SenderNodeId != batch.SenderNodeId
                        || ack.ReceiverNodeId != batch.ReceiverNodeId || ack.Epoch != batch.Epoch
                        || ack.Sequence < 0 || ack.Sequence >= batch.Sequence - 1 || ack.ExpectedSequence != ack.Sequence + 1)
                        throw new InvalidDataException("Invalid runtime sync gap response");
                    IReadOnlyList<RuntimeStateSyncBatch> retainedRange;
                    try { retainedRange = _sender.ReadRange(batch, ack.ExpectedSequence); }
                    catch (RuntimeStateSyncHistoryUnavailableException) { await Bootstrap(batch, ack.Epoch); return; }
                    foreach (var retained in retainedRange)
                        await Deliver(retained, confirm: false, repairGap: false);
                    await Deliver(batch, confirm: true, repairGap: false);
                    return;
                }
                if (ack.Status != "Applied" || ack.ClusterId != batch.ClusterId || ack.SenderNodeId != batch.SenderNodeId
                    || ack.ReceiverNodeId != batch.ReceiverNodeId || ack.Epoch != batch.Epoch
                    || ack.Sequence != batch.Sequence || ack.Digest != batch.Digest)
                    throw new InvalidDataException("Runtime sync ACK does not confirm transmitted batch");
                if (confirm)
                {
                    _sender.Confirm(batch, ack);
                    foreach (var mutation in batch.Mutations)
                        (telemetry ?? NodeCommunicationTelemetry.Shared).ObserveReplicationAcknowledgement(mutation.ChangedAt);
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task RecoverAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                var peers = options.CurrentValue.Peers.Where(p => p.Value.Enabled && p.Value.DurableSyncEnabled).Select(p => p.Key).ToArray();
                await Parallel.ForEachAsync(peers, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (peer, stop) =>
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    budget.CancelAfter(TimeSpan.FromSeconds(30));
                    try { await SendAsync(peer, null, budget.Token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or JsonException or UnauthorizedAccessException or OperationCanceledException)
                    { NLog.LogManager.GetCurrentClassLogger().Warn("Runtime sync pending retry failed: {Peer} ({ErrorType})", peer, ex.GetType().Name); }
                });
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
