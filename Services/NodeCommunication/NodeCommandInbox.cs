using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeInboxResult(NodeAcknowledgement? Acknowledgement, NodeProtocolError? Error);

public sealed class NodeCommandInbox(IOptionsMonitor<NodeCommunicationOptions> options)
{
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;
    private const int MaxRecordBytes = 1024 * 1024;

    public async Task<NodeInboxResult> ExecuteAsync(NodeMessage message,
        Func<Task<NodeAcknowledgement>> execute, CancellationToken cancellationToken,
        Func<Task<NodeAcknowledgement?>>? reconcile = null, bool queryOnly = false)
    {
        var directory = Path.Combine(Path.GetFullPath(options.CurrentValue.StateDirectory), "inbox");
        Directory.CreateDirectory(directory);
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { message.ClusterId, message.SenderNodeId, message.ReceiverNodeId, message.MessageId });
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(identity)) + ".json");
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            message.Type, message.CorrelationId, message.Payload
        })));
        FileStream gate;
        try { gate = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return new(null, new("node_inbox_busy", 503)); }
        await using var held = gate;
        if (File.Exists(path))
        {
            InboxRecord? existing;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 1024 * 1024) return new(null, new("node_inbox_corrupt", 503));
                existing = await JsonSerializer.DeserializeAsync<InboxRecord>(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException) { return new(null, new("node_inbox_corrupt", 503)); }
            catch (IOException) { return new(null, new("node_inbox_unavailable", 503)); }
            catch (UnauthorizedAccessException) { return new(null, new("node_inbox_unavailable", 503)); }
            if (existing?.Digest is not { Length: 64 } || !existing.Digest.All(Uri.IsHexDigit))
                return new(null, new("node_inbox_corrupt", 503));
            if (existing.Digest != digest) return new(null, new("node_message_conflict", 409));
            if (existing.Retired) return new(null, new("node_result_retired", 409));
            if (existing.Result is null && reconcile is not null)
            {
                var recovered = await reconcile();
                if (recovered is not null)
                {
                    Save(path, new(digest, recovered, DateTimeOffset.UtcNow));
                    return new(recovered, null);
                }
            }
            if (existing.Result is not null) LyWaf.Services.Telemetry.NodeCommunicationTelemetry.Shared.Event("idempotency_hit");
            return existing.Result is null
                ? new(null, new("node_outcome_unknown", 409))
                : new(existing.Result, null);
        }
        if (queryOnly) return new(null, new("node_outcome_unknown", 409));
        if (message.ExpiresAt <= DateTimeOffset.UtcNow) return new(null, new("node_message_expired", 409));
        if (Directory.EnumerateFiles(directory, "*.json").Take(10000).Count() >= 10000)
            return new(null, new("node_inbox_full", 503));
        // Include orphan temporary files and reserve a full outcome before any side effect.
        var intentBytes = JsonSerializer.SerializeToUtf8Bytes(new InboxRecord(digest, null)).Length;
        if (DirectoryBytes(directory) > MaxDirectoryBytes - MaxRecordBytes - intentBytes)
            return new(null, new("node_inbox_full", 503));
        cancellationToken.ThrowIfCancellationRequested();
        Save(path, new(digest, null));
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ExpiresAt <= DateTimeOffset.UtcNow) return new(null, new("node_message_expired", 409));
        var result = await execute();
        // Once the command has finished, persist its outcome even if the HTTP caller disconnected.
        Save(path, new(digest, result, DateTimeOffset.UtcNow));
        return new(result, null);
    }

    public async Task<int> RetireCompletedAsync(CancellationToken token)
    {
        var directory = Path.Combine(Path.GetFullPath(options.CurrentValue.StateDirectory), "inbox");
        Directory.CreateDirectory(directory);
        token.ThrowIfCancellationRequested();
        await using var gate = new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            InboxRecord? record;
            await using (var stream = File.OpenRead(path))
            {
                if (stream.Length > 1024 * 1024) throw new InvalidDataException("Inbox record too large");
                record = await JsonSerializer.DeserializeAsync<InboxRecord>(stream, cancellationToken: token);
            }
            if (record?.Digest is not { Length: 64 } || !record.Digest.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid inbox record");
            if (record.Retired || record.Result is null || record.CompletedAt is not { } completed
                || completed > DateTimeOffset.UtcNow.AddDays(-7)) continue;
            token.ThrowIfCancellationRequested();
            Save(path, record with { Result = null, Retired = true });
            if (++count >= 100) break;
        }
        return count;
    }

    private static void Save(string path, InboxRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > MaxRecordBytes)
            throw new IOException("Inbox record exceeds its storage budget");
        // Atomic replacement temporarily needs both the old and the new record on disk.
        if (DirectoryBytes(Path.GetDirectoryName(path)!) > MaxDirectoryBytes - bytes.Length)
            throw new IOException("Inbox storage budget exhausted");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static long DirectoryBytes(string directory)
    {
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            total = checked(total + new FileInfo(path).Length);
            if (total > MaxDirectoryBytes) break;
        }
        return total;
    }

    private sealed record InboxRecord(string Digest, NodeAcknowledgement? Result,
        DateTimeOffset? CompletedAt = null, bool Retired = false);
}
