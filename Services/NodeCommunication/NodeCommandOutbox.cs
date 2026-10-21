using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeOutboxRecord(NodeMessage Message, string Endpoint, int Attempts = 0,
    string State = "Pending", NodeAcknowledgement? Result = null, int? StatusCode = null,
    DateTimeOffset? NextAttemptAt = null, DateTimeOffset? CompletedAt = null, bool Archived = false,
    string? ErrorCode = null, string? MessageKey = null, string? RemoteTraceId = null, TimeSpan? RetryAfter = null);

internal sealed record NodeOutboxArchive(NodeOutboxRecord Record, string Digest);

/// <summary>Per-peer single-writer commands with a short, shared metadata/quota lock.</summary>
public sealed class NodeCommandOutbox(string stateDirectory)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(stateDirectory), "outbox");
    public const int MaximumAttempts = 3;
    private const long MaximumBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan MetadataLockTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<NodeOutboxRecord> ReadRecords()
    {
        Directory.CreateDirectory(_directory);
        using var gate = LockMetadata();
        var records = Directory.EnumerateFiles(_directory, "*.json").ToDictionary(path => Path.GetFileName(path), Read);
        var archive = Path.Combine(_directory, "archive");
        if (Directory.Exists(archive))
            foreach (var path in Directory.EnumerateFiles(archive, "*.json"))
                records[Path.GetFileName(path)] = ReadArchive(path).Record;
        return records.Values.ToArray();
    }

    /// <summary>Compact confirmed results only. Unknown outcomes and replay fences never age out.</summary>
    public async Task<int> ArchiveCompletedAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        string[] paths;
        using (await LockMetadataAsync(token)) paths = Directory.GetFiles(_directory, "*.json");
        var count = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            NodeOutboxRecord record;
            using (await LockMetadataAsync(token))
            {
                if (!File.Exists(path)) continue;
                record = Read(path);
                if (!CanArchive(record)) continue;
            }
            FileStream peerGate;
            try { peerGate = OpenLock(PeerLockPath(record.Message)); }
            catch (IOException ex) when (SharingViolation(ex)) { continue; }
            using (peerGate)
            using (await LockMetadataAsync(token))
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) continue;
                record = Read(path);
                if (!CanArchive(record)) continue;
                var destination = Path.Combine(_directory, "archive", Path.GetFileName(path));
                var digest = Digest(record.Message);
                if (File.Exists(destination))
                {
                    var existing = ReadArchive(destination);
                    if (existing.Digest != digest || existing.Record.Endpoint != record.Endpoint
                        || existing.Record.Result != record.Result)
                        throw new InvalidDataException("Conflicting outbox archive");
                }
                else
                {
                    var compact = record with
                    {
                        Archived = true, NextAttemptAt = null,
                        Message = record.Message with { Payload = JsonSerializer.SerializeToElement<object?>(null) }
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    SaveBytesLocked(destination, JsonSerializer.SerializeToUtf8Bytes(new NodeOutboxArchive(compact, digest)));
                }
                // The archive is the replay fence. Never delete the source before it is durable.
                token.ThrowIfCancellationRequested();
                File.Delete(path);
                if (++count >= 100) break;
            }
        }
        return count;
    }

    private static bool CanArchive(NodeOutboxRecord record) => record.State is "Completed" or "Rejected" && record.Result is not null
        && record.CompletedAt is { } completed && completed >= record.Message.CreatedAt
        && completed <= DateTimeOffset.UtcNow.AddDays(-7);

    private string PeerLockPath(NodeMessage message)
    {
        var directory = Path.Combine(_directory, "peer-locks");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { message.ClusterId, message.ReceiverNodeId }))) + ".lock");
    }

    public async Task<NodeAcknowledgement> ExecuteAsync(NodeMessage message, string endpoint,
        Func<NodeMessage, bool, CancellationToken, Task<NodeAcknowledgement>> send, CancellationToken token,
        bool recoveryOnly = false)
    {
        Directory.CreateDirectory(_directory);
        token.ThrowIfCancellationRequested();
        // Keep lock files: unlinking an unlocked file can race a new owner and split the lock.
        using var peerGate = OpenLock(PeerLockPath(message));
        var path = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { message.ClusterId, message.SenderNodeId, message.ReceiverNodeId, message.MessageId }))) + ".json");
        NodeOutboxRecord record;
        using (await LockMetadataAsync(token))
        {
            token.ThrowIfCancellationRequested();
            var archivePath = Path.Combine(_directory, "archive", Path.GetFileName(path));
            if (File.Exists(archivePath))
            {
                var archive = ReadArchive(archivePath);
                var stored = archive.Record.Message;
                if (stored.ClusterId != message.ClusterId || stored.SenderNodeId != message.SenderNodeId
                    || stored.ReceiverNodeId != message.ReceiverNodeId || stored.MessageId != message.MessageId
                    || stored.Type != message.Type || stored.CorrelationId != message.CorrelationId
                    || archive.Record.Endpoint != endpoint || (!recoveryOnly && archive.Digest != Digest(message)))
                    throw new InvalidOperationException("Archived outbox identity or content conflict");
                return archive.Record.Result!;
            }
            var exists = File.Exists(path);
            if (recoveryOnly && !exists) throw new InvalidOperationException("Recovery requires an existing attempted command");
            record = exists ? Read(path) : new NodeOutboxRecord(message, endpoint);
            if (recoveryOnly && record.Attempts == 0) throw new InvalidOperationException("Recovery cannot send an unattempted command");
            if (record.Endpoint != endpoint || Digest(record.Message) != Digest(message))
                throw new InvalidOperationException("Outbox command identity reused with different content or endpoint");
            if (!exists) SaveLocked(path, record);
        }
        if (record.Result is not null) return record.Result;
        if (record.State is "Rejected" or "Stopped")
        {
            var error = new HttpRequestException("Node command rejected", null, (HttpStatusCode?)record.StatusCode);
            RestoreMetadata(error, "ErrorCode", record.ErrorCode, 128);
            RestoreMetadata(error, "MessageKey", record.MessageKey, 140);
            RestoreMetadata(error, "RemoteTraceId", record.RemoteTraceId, 128);
            if (record.RetryAfter is { } retryAfter) error.Data["RetryAfter"] = retryAfter;
            throw error;
        }
        while (record.Attempts < MaximumAttempts && DateTimeOffset.UtcNow < record.Message.ExpiresAt)
        {
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (record.NextAttemptAt is { } next && next > now)
                await Task.Delay((next < record.Message.ExpiresAt ? next : record.Message.ExpiresAt) - now, token);
            if (DateTimeOffset.UtcNow >= record.Message.ExpiresAt) break;
            var queryOnly = recoveryOnly || record.Attempts > 0;
            record = record with { Attempts = record.Attempts + 1, State = "Unknown", NextAttemptAt = null };
            await SaveAsync(path, record, token);
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= record.Message.ExpiresAt) break;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remaining = record.Message.ExpiresAt - DateTimeOffset.UtcNow;
            deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                if (queryOnly) LyWaf.Services.Telemetry.NodeCommunicationTelemetry.Shared.Event("retry_query");
                var result = await send(record.Message, queryOnly, deadline.Token);
                await SaveAsync(path, record with { State = result.Ack ? "Completed" : "Rejected", Result = result,
                    ErrorCode = result.ErrorCode, MessageKey = result.MessageKey,
                    StatusCode = result.Ack ? null : NodeConfigurationErrors.HttpStatus(result.ErrorCode),
                    CompletedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } status && !Transient(status))
            {
                await SaveAsync(path, record with
                {
                    State = queryOnly || ex.Data["ErrorCode"] is "node_outcome_unknown" or NodeConfigurationErrors.Unknown
                        ? "Stopped" : "Rejected", StatusCode = (int)status,
                    ErrorCode = Identifier(ex.Data["ErrorCode"], 128),
                    MessageKey = Identifier(ex.Data["MessageKey"], 140),
                    RemoteTraceId = Identifier(ex.Data["RemoteTraceId"], 128),
                    RetryAfter = ex.Data["RetryAfter"] is TimeSpan retry ? retry : null
                }, CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, record.Attempts - 1) + Random.Shared.Next(250));
                if (ex.Data["RetryAfter"] is TimeSpan retryAfter && retryAfter > delay) delay = retryAfter;
                record = record with { NextAttemptAt = DateTimeOffset.UtcNow + delay };
                await SaveAsync(path, record, CancellationToken.None);
                token.ThrowIfCancellationRequested();
            }
        }
        throw new InvalidOperationException("Node command outcome unknown; outbox retry budget or deadline exhausted");
    }

    private static string? Identifier(object? value, int maximum) => value is string text
        && text.Length > 0 && text.Length <= maximum
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') ? text : null;

    private static void RestoreMetadata(Exception error, string key, string? value, int maximum)
    {
        if (Identifier(value, maximum) is { } safe) error.Data[key] = safe;
    }

    private static FileStream OpenLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static bool SharingViolation(IOException error) => (error.HResult & 0xffff) is 11 or 32 or 33;

    private FileStream LockMetadata()
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return OpenLock(Path.Combine(_directory, ".lock")); }
            catch (IOException ex) when (SharingViolation(ex) && elapsed.Elapsed < MetadataLockTimeout)
            { Thread.Sleep(10); }
        }
    }

    private async Task<FileStream> LockMetadataAsync(CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return OpenLock(Path.Combine(_directory, ".lock")); }
            catch (IOException ex) when (SharingViolation(ex) && elapsed.Elapsed < MetadataLockTimeout)
            { await Task.Delay(10, token); }
        }
    }

    private async Task SaveAsync(string path, NodeOutboxRecord record, CancellationToken token)
    {
        using var gate = await LockMetadataAsync(token);
        token.ThrowIfCancellationRequested();
        SaveLocked(path, record);
    }
    private static bool Transient(HttpStatusCode status) => (int)status is 408 or 429 or 502 or 503 or 504;
    private static string Digest(NodeMessage message) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { message.Type, message.CorrelationId, message.Payload })));
    private static NodeOutboxRecord Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 24 * 1024 * 1024) throw new InvalidDataException("Outbox record too large");
        var record = JsonSerializer.Deserialize<NodeOutboxRecord>(stream) ?? throw new InvalidDataException("Invalid outbox record");
        if (record.Attempts is < 0 or > MaximumAttempts || record.Message.ExpiresAt <= record.Message.CreatedAt)
            throw new InvalidDataException("Invalid outbox record");
        return record;
    }
    private static NodeOutboxArchive ReadArchive(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 24 * 1024 * 1024) throw new InvalidDataException("Outbox archive too large");
        var archive = JsonSerializer.Deserialize<NodeOutboxArchive>(stream) ?? throw new InvalidDataException("Invalid outbox archive");
        if (archive.Digest is not { Length: 64 } || !archive.Digest.All(Uri.IsHexDigit)
            || !archive.Record.Archived || archive.Record.State != "Completed" || archive.Record.Result is null
            || archive.Record.CompletedAt is null || archive.Record.Attempts is < 1 or > MaximumAttempts)
            throw new InvalidDataException("Invalid outbox archive");
        return archive;
    }
    // Called only with the metadata lock. Never acquire a peer lock while holding it.
    private static void SaveLocked(string path, NodeOutboxRecord record)
        => SaveBytesLocked(path, JsonSerializer.SerializeToUtf8Bytes(record));

    private static void SaveBytesLocked(string path, byte[] bytes)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) && directory.EnumerateFiles("*.json").Take(10000).Count() >= 10000)
            throw new IOException("Node outbox capacity exceeded");
        // Include the replacement temporary file in the quota: acknowledgement growth is bounded too.
        if (directory.GetFiles().Sum(f => f.Length) + bytes.Length > MaximumBytes)
            throw new IOException("Node outbox capacity exceeded");
        try
        {
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(path + ".tmp", path, true);
        }
        finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
    }
}
