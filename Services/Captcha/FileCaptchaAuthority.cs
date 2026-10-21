using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Captcha;

// One authority owns this directory. Sequence gaps after a crash are harmless; reuse is forbidden.
internal sealed class FileCaptchaAuthority(IOptions<CaptchaStoreOptions> options, TimeProvider? clock = null) : IDisposable
{
    private readonly CaptchaStoreOptions _options = options.Value;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Record> _consumed = new(StringComparer.Ordinal);
    private FileStream? _lease;
    private long _sequence, _bytes;
    private int _waiters;
    private bool _faulted;
    private string Root => Path.GetFullPath(_options.StateDirectory);
    internal sealed record Record(string Token, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
        long Sequence, CaptchaService.CaptchaEntry? Entry,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ConsumerNodeId = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SubjectHash = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ConsumedAt = null);
    private sealed record Counter(string ClusterId, string AuthorityNodeId, long Sequence);

    internal static bool ValidToken(string? token) => token is { Length: 35 }
        && (token.StartsWith("r1.", StringComparison.Ordinal) || token.StartsWith("p1.", StringComparison.Ordinal))
        && !token.AsSpan(3).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan());
    private string PathFor(string token) => Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) + ".json");
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void RejectLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Challenge state cannot be a link");
    }
    private static void Write(string path, byte[] bytes)
    {
        RejectLink(path);
        var temp = path + ".tmp";
        RejectLink(temp);
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void SaveCounter(long value) => Write(Path.Combine(Root, "sequence"),
        JsonSerializer.SerializeToUtf8Bytes(new Counter(_options.ClusterId, _options.AuthorityNodeId, value)));
    private void Initialize()
    {
        if (_faulted) throw new IOException("Challenge authority requires recovery");
        if (_lease is not null) return;
        if (Root.Equals(Path.GetPathRoot(Root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Challenge state requires a dedicated directory");
        Directory.CreateDirectory(Root);
        RejectLink(Root);
        var leasePath = Path.Combine(Root, ".owner");
        RejectLink(leasePath);
        _lease = new(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var files = Directory.EnumerateFiles(Root).Take(_options.MaxChallenges * 2 + 10).ToArray();
        if (files.Length >= _options.MaxChallenges * 2 + 10) throw new IOException("Challenge directory exceeds capacity");
        foreach (var file in files)
        {
            RejectLink(file);
            var name = Path.GetFileName(file);
            var record = name.EndsWith(".tmp", StringComparison.Ordinal) ? name[..^4] : name;
            if (name is not (".owner" or "sequence" or "sequence.tmp")
                && !(record.Length == 69 && record.EndsWith(".json", StringComparison.Ordinal)
                    && record.AsSpan(0, 64).ToString().All(char.IsAsciiHexDigit)))
                throw new IOException("Challenge state requires a dedicated directory");
        }
        var sequencePath = Path.Combine(Root, "sequence");
        if (!File.Exists(sequencePath))
        {
            if (files.Any(p => p.EndsWith(".json", StringComparison.Ordinal) || p.EndsWith(".json.tmp", StringComparison.Ordinal)))
                throw new IOException("Missing challenge sequence");
            SaveCounter(0);
        }
        RejectLink(sequencePath);
        if (new FileInfo(sequencePath).Length > 1024) throw new IOException("Invalid challenge sequence");
        var state = JsonSerializer.Deserialize<Counter>(File.ReadAllBytes(sequencePath));
        if (state is null || state.ClusterId != _options.ClusterId || state.AuthorityNodeId != _options.AuthorityNodeId || state.Sequence < 0)
            throw new IOException("Challenge authority identity or sequence mismatch");
        _sequence = state.Sequence;
        foreach (var file in files)
        {
            RejectLink(file);
            if (file.EndsWith(".tmp", StringComparison.Ordinal)) { File.Delete(file); continue; }
            if (!file.EndsWith(".json", StringComparison.Ordinal))
            {
                if (Path.GetFileName(file) is not ("sequence" or ".owner")) throw new IOException("Unexpected challenge state file");
                continue;
            }
            var size = new FileInfo(file).Length;
            if (size > 8192 || _bytes + size > _options.MaxBytes - 10240 || _records.Count >= _options.MaxChallenges)
                throw new IOException("Challenge directory exceeds capacity");
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(file));
            if (record is null || !ValidToken(record.Token) || PathFor(record.Token) != file
                || record.Sequence <= 0 || record.Sequence > _sequence || record.ExpiresAt <= record.IssuedAt
                || record.ExpiresAt - record.IssuedAt > TimeSpan.FromMinutes(10)) throw new IOException("Invalid challenge record");
            if ((record.ConsumerNodeId is not null || record.SubjectHash is not null || record.ConsumedAt is not null)
                && (record.Entry is not null || !record.Token.StartsWith("r1.", StringComparison.Ordinal)
                    || !LyWaf.Services.NodeCommunication.NodeMessageAuthentication.ValidId(record.ConsumerNodeId)
                    || record.SubjectHash is not { Length: 64 } || !record.SubjectHash.All(char.IsAsciiHexDigit)
                    || record.ConsumedAt is null || record.ConsumedAt < record.IssuedAt || record.ConsumedAt >= record.ExpiresAt))
                throw new IOException("Invalid challenge consumption receipt");
            _records.Add(record.Token, record);
            if (record.ConsumerNodeId is not null) _consumed[Digest(record.Token)] = record;
            _bytes += size;
        }
        Cleanup();
    }
    private void Cleanup()
    {
        foreach (var pair in _records.Where(p => p.Value.ExpiresAt <= _clock.GetUtcNow()).Take(256).ToArray())
        {
            var path = PathFor(pair.Key);
            var size = new FileInfo(path).Length;
            File.Delete(path);
            _records.Remove(pair.Key);
            _consumed.Remove(Digest(pair.Key));
            _bytes -= size;
        }
    }
    private void Persist(Record record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var path = PathFor(record.Token);
        var previous = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (bytes.Length > 8192 || _bytes + bytes.Length > _options.MaxBytes - 2048
            || _bytes + bytes.Length - previous > _options.MaxBytes - 10240)
            throw new CaptchaStoreUnavailableException();
        // Publish the durable high-water mark before its record; a lost reply cannot reuse the sequence.
        SaveCounter(record.Sequence);
        _sequence = record.Sequence;
        Write(path, bytes);
        _records[record.Token] = record;
        if (record.ConsumerNodeId is not null) _consumed[Digest(record.Token)] = record;
        _bytes += bytes.Length - previous;
    }
    internal long Sequence => _sequence;
    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _waiters) > 256)
        { Interlocked.Decrement(ref _waiters); throw new CaptchaStoreUnavailableException(); }
        try
        {
            if (!await _gate.WaitAsync(TimeSpan.FromMilliseconds(_options.TimeoutMilliseconds), cancellationToken))
                throw new CaptchaStoreUnavailableException();
        }
        finally { Interlocked.Decrement(ref _waiters); }
    }
    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        await AcquireAsync(cancellationToken);
        try { Initialize(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _faulted = true; throw new CaptchaStoreUnavailableException(); }
        finally { _gate.Release(); }
    }

    public async Task PutAsync(string token, CaptchaService.CaptchaEntry entry, DateTimeOffset issuedAt,
        int lifetimeMilliseconds, CancellationToken cancellationToken)
    {
        if (!ValidToken(token) || lifetimeMilliseconds is < 1 or > 600000 || entry is null
            || JsonSerializer.SerializeToUtf8Bytes(entry).Length > 4096) throw new ArgumentException("Invalid challenge payload");
        await AcquireAsync(cancellationToken);
        try
        {
            Initialize(); Cleanup();
            var now = _clock.GetUtcNow();
            if (issuedAt < now.AddSeconds(-60) || issuedAt > now.AddSeconds(60)
                || issuedAt.AddMilliseconds(lifetimeMilliseconds) <= now) throw new ArgumentException("Expired challenge issuance");
            // Never extend or resurrect an issued/consumed token, even with a fresh transport nonce.
            if (_records.ContainsKey(token)) throw new ArgumentException("Challenge token already issued");
            if (_records.Count >= _options.MaxChallenges) throw new CaptchaStoreUnavailableException();
            cancellationToken.ThrowIfCancellationRequested();
            Persist(new(token, now, now.AddMilliseconds(lifetimeMilliseconds), checked(_sequence + 1), entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        { _faulted = true; throw new CaptchaStoreUnavailableException(); }
        finally { _gate.Release(); }
    }

    public async Task<CaptchaService.CaptchaEntry?> TakeAsync(string token, CancellationToken cancellationToken, string? consumerNodeId = null)
    {
        if (!ValidToken(token)) return null;
        if (consumerNodeId is not null && !LyWaf.Services.NodeCommunication.NodeMessageAuthentication.ValidId(consumerNodeId))
            throw new ArgumentException("Invalid challenge consumer");
        await AcquireAsync(cancellationToken);
        try
        {
            Initialize(); Cleanup();
            var now = _clock.GetUtcNow();
            if (!_records.TryGetValue(token, out var current) || current.Entry is null || current.ExpiresAt <= now) return null;
            cancellationToken.ThrowIfCancellationRequested();
            // Erase the answer and persist the consumed fence before releasing it to the caller.
            var receipt = consumerNodeId is not null && token.StartsWith("r1.", StringComparison.Ordinal)
                && current.Entry.Mode is "math" or "slider" && current.Entry.ClientIp is { Length: > 0 and <= 2048 };
            Persist(current with { Sequence = checked(_sequence + 1), Entry = null,
                ConsumerNodeId = receipt ? consumerNodeId : null,
                SubjectHash = receipt ? Digest(current.Entry.ClientIp) : null,
                ConsumedAt = receipt ? now : null });
            return current.Entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        { _faulted = true; throw new CaptchaStoreUnavailableException(); }
        finally { _gate.Release(); }
    }

    internal async Task<bool> WasConsumedAsync(string challengeDigest, string consumer, string subject, CancellationToken token)
    {
        if (challengeDigest is not { Length: 64 } || !challengeDigest.All(char.IsAsciiHexDigit)
            || subject is not { Length: > 0 and <= 2048 }) return false;
        await AcquireAsync(token);
        try
        {
            Initialize(); Cleanup();
            var now = _clock.GetUtcNow();
            return _consumed.TryGetValue(challengeDigest.ToUpperInvariant(), out var receipt)
                && receipt.ConsumerNodeId == consumer && receipt.SubjectHash == Digest(subject)
                && receipt.ExpiresAt > now && receipt.ConsumedAt >= now.AddMinutes(-2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _faulted = true; throw new CaptchaStoreUnavailableException(); }
        finally { _gate.Release(); }
    }
    public void Dispose() { _lease?.Dispose(); _gate.Dispose(); }
}
