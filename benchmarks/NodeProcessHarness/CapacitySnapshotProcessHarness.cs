using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;

static partial class CapacityProcessHarness
{
    static void ConfigureSnapshotHost(WebApplication app, RuntimeStateOptions runtime, RuntimeStateStore store, string root)
    {
        app.MapPost("/test/snapshot/seed/{count:int}", async (int count) =>
        {
            if (!Path.GetFileName(root).StartsWith("snapshot", StringComparison.Ordinal) || count is < 1 or > 1100)
                return Results.BadRequest();
            var value = new string('x', 50_000);
            for (var i = 0; i < count; i++)
            {
                store.Set("capacity", "large-" + i, value);
                if (i % 16 == 0) await Until(() => store.GetStatus().QueueDepth == 0);
            }
            await Until(() => store.GetStatus().QueueDepth == 0);
            Check(store.GetStatus().DroppedMutations == 0, "snapshot seed must not lose journal entries");
            return Results.Json(new { count, status = store.GetStatus() });
        });
        app.MapPost("/test/snapshot/revoke/{revoked:bool}", (bool revoked) =>
        { runtime.Peers["control"].AllowBootstrapExport = !revoked; return Results.Ok(); });
        app.MapPost("/test/snapshot/expire/{expired:bool}", (bool expired, RuntimeStateSyncReceiver receiver) =>
        {
            typeof(RuntimeStateSyncReceiver).GetProperty("UtcNow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(receiver, (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow.AddMinutes(expired ? 6 : 0)));
            return Results.Ok();
        });
    }

    public static async Task RunSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-snapshot-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string message) { Console.WriteLine(message); log.WriteLine($"{DateTimeOffset.UtcNow:O} {message}"); }
        Record("ROOT " + root);
        Record("LyWaf.dll SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeStateStore).Assembly.Location))));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Host = "localhost:18937";
        var scope = new RuntimeStateBootstrapScope(["capacity"], [], [], true);
        var scopeDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope, Json)));
        RuntimeStateSyncBatch Batch(string phase, string id, string epoch, string? digest = null, int? page = null)
        {
            var batch = new RuntimeStateSyncBatch("capacity-test", "control", "edge", epoch, 1, [], "",
                new(phase, id, scopeDigest, digest, page));
            return batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        }
        async Task<RuntimeStateSyncAck?> Exchange(Child child, RuntimeStateSyncBatch batch, HttpStatusCode status = HttpStatusCode.OK)
        {
            var envelope = Message("runtime.sync", batch);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, child.Address + NodeMessageAuthentication.Prefix + "runtime.sync") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, envelope, "test", Secret, bytes);
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();
            Check(response.StatusCode == status, $"snapshot {batch.Bootstrap!.Phase}: expected {(int)status}, received {(int)response.StatusCode}");
            if (status != HttpStatusCode.OK)
            {
                var error = JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
                Check(error == (status == HttpStatusCode.Forbidden ? "node_operation_forbidden" : "node_runtime_store_unavailable"), "structured snapshot failure");
                Record($"HTTP {batch.Bootstrap!.Phase} {(int)status} {error}");
                return null;
            }
            return JsonSerializer.Deserialize<RuntimeStateSyncAck>(body, Json)!;
        }
        async Task Post(Child child, string path)
        { using var response = await http.PostAsync(child.Address + path, null); response.EnsureSuccessStatusCode(); }

        // Fixed-size records bracket the actual staging limit without claiming every possible encoding has the same limit.
        foreach (var count in new[] { 960, 1000, 1004 })
        {
            var directory = Path.Combine(root, "snapshot-" + count); Directory.CreateDirectory(directory);
            Child? child = null;
            try
            {
                child = await Start(directory, Record);
                await Post(child, "/test/snapshot/seed/" + count);
                var sync = Path.Combine(directory, "runtime", "runtime-sync");
                var id = Guid.NewGuid().ToString("N"); var epoch = Guid.NewGuid().ToString("N");
                var offered = await Exchange(child, Batch("fetch", id, epoch), count < 1004 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
                Check(Bytes(sync) <= 64 * MiB, "snapshot staging bounded");
                if (offered is null)
                {
                    Check(Directory.GetFiles(sync, "snapshot-offer-*.json").Length == 0, "failed capture never publishes manifest");
                    Check(Directory.GetFiles(sync, "in-*.json").Length == 0, "failed capture never advances receipt");
                    var before = Bytes(sync);
                    child.Kill(); await child.DisposeAsync(); child = await Start(directory, Record);
                    Check(Bytes(sync) == before, "restart preserves failed-capture evidence");
                    await Exchange(child, Batch("fetch", id, epoch), HttpStatusCode.ServiceUnavailable);
                    Check(Bytes(sync) <= 64 * MiB, "retry cannot bypass staging quota");
                    Record($"PASS rejected count={count} valueBytes=50000 stagingBytes={Bytes(sync)}; no manifest/receipt after restart");
                    continue;
                }
                Check(offered.Status == "SnapshotOffered" && offered.Manifest is not null, "snapshot offered");
                var manifest = offered.Manifest!;
                var manifestPath = Directory.GetFiles(sync, "snapshot-offer-*.json").Single();
                var manifestBytes = File.ReadAllBytes(manifestPath);
                Record($"OFFER count={count} rawBytes={manifest.ByteLength} pages={manifest.PageDigests.Length} stagingBytes={Bytes(sync)} rootBytes={Bytes(Path.Combine(directory, "runtime"))}");
                // Verify every byte over the real endpoint, not by reading authoritative page files directly.
                using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                for (var page = 0; page < manifest.PageDigests.Length; page++)
                {
                    var response = await Exchange(child, Batch("page", id, epoch, manifest.Header.Digest, page));
                    var item = response!.Page!;
                    Check(response.Status == "SnapshotPage" && item.SnapshotId == id && item.ImageDigest == manifest.Header.Digest && item.Index == page,
                        "page is bound to immutable snapshot");
                    Check(item.Data.Length <= RuntimeStateBootstrapManifest.PageBytes
                        && Convert.ToHexString(SHA256.HashData(item.Data)) == manifest.PageDigests[page], "bounded valid page");
                    contentHash.AppendData(item.Data); received += item.Data.Length;
                }
                Check(received == manifest.ByteLength && Convert.ToHexString(contentHash.GetHashAndReset()) == manifest.ContentDigest, "full HTTP content digest");
                // New capture must coexist with the previous authoritative offer until publication.
                var beforeReplacement = Bytes(sync);
                await Exchange(child, Batch("fetch", Guid.NewGuid().ToString("N"), epoch), HttpStatusCode.ServiceUnavailable);
                Check(File.ReadAllBytes(manifestPath).SequenceEqual(manifestBytes), "failed replacement leaves old offer byte-identical");
                Check(Bytes(sync) <= 64 * MiB, "old offer plus partial new export bounded");
                if (count == 960) Check(Bytes(sync) > beforeReplacement, "replacement writes partial pages before quota denial");
                Record($"REPLACEMENT count={count} stagedBefore={beforeReplacement} stagedAfter={Bytes(sync)} partialBytes={Bytes(sync) - beforeReplacement}");
                child.Kill(); await child.DisposeAsync(); child = await Start(directory, Record);
                var restored = await Exchange(child, Batch("fetch", id, epoch));
                Check(restored!.Manifest!.ContentDigest == manifest.ContentDigest && restored.Manifest.Header.NewEpoch == manifest.Header.NewEpoch,
                    "manifest identity and epoch survive process kill");
                var pageBatch = Batch("page", id, epoch, manifest.Header.Digest, 0);
                Check((await Exchange(child, pageBatch))!.Status == "SnapshotPage", "old page readable after failed replacement and restart");
                await Post(child, "/test/snapshot/revoke/true");
                await Exchange(child, pageBatch, HttpStatusCode.Forbidden);
                await Post(child, "/test/snapshot/revoke/false");
                Check((await Exchange(child, pageBatch))!.Status == "SnapshotPage", "explicit regrant restores valid offer access");
                await Post(child, "/test/snapshot/expire/true");
                Check((await Exchange(child, pageBatch))!.Status == "Conflict", "expired offer page rejected");
                Check((await Exchange(child, Batch("commit", id, manifest.Header.NewEpoch, manifest.Header.Digest)))!.Status == "Conflict", "expired offer cannot advance epoch");
                Check(Directory.GetFiles(sync, "in-*.json").Length == 0, "revoked/expired/failed capture has no ACK receipt");
                Check(File.ReadAllBytes(manifestPath).SequenceEqual(manifestBytes), "denied reads never mutate manifest");
                Record($"PASS offered count={count}; all pages verified, replacement quota failure, restart, revocation, expiry; stagingBytes={Bytes(sync)}");
            }
            finally { if (child is not null) await child.DisposeAsync(); }
        }
        Record("PASS snapshot capacity/offer recovery scenarios");
    }
}
