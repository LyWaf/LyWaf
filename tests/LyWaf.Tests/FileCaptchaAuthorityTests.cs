using LyWaf.Services.Captcha;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class FileCaptchaAuthorityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "captcha-files-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private CaptchaStoreOptions Options(int capacity = 100) => new()
        { StateDirectory = _directory, ClusterId = "test", AuthorityNodeId = "authority", MaxChallenges = capacity };
    private FileCaptchaAuthority Store(int capacity = 100) => new(Microsoft.Extensions.Options.Options.Create(Options(capacity)), _clock);
    private static string Token() => "r1." + Guid.NewGuid().ToString("N");
    private static CaptchaService.CaptchaEntry Entry() => new() { Answer = "42", ClientIp = "192.0.2.7", Mode = "math" };

    [Fact]
    public async Task ConsumptionReceiptIsDurableAndBoundToVerifierAndSubject()
    {
        var token = Token();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        using (var store = Store())
        {
            await store.PutAsync(token, Entry(), _clock.GetUtcNow(), 300000, default);
            Assert.False(await store.WasConsumedAsync(digest, "node-a", "192.0.2.7", default));
            Assert.NotNull(await store.TakeAsync(token, default, "node-a"));
            Assert.Null(await store.TakeAsync(token, default, "node-b"));
            Assert.True(await store.WasConsumedAsync(digest.ToLowerInvariant(), "node-a", "192.0.2.7", default));
            Assert.False(await store.WasConsumedAsync(digest, "node-b", "192.0.2.7", default));
            Assert.False(await store.WasConsumedAsync(digest, "node-a", "192.0.2.8", default));
        }
        using var restarted = Store();
        Assert.True(await restarted.WasConsumedAsync(digest, "node-a", "192.0.2.7", default));
        _clock.Now = _clock.Now.AddSeconds(121);
        Assert.False(await restarted.WasConsumedAsync(digest, "node-a", "192.0.2.7", default));
    }

    [Theory]
    [InlineData("p1.", "math")]
    [InlineData("r1.", "other")]
    public async Task UnrelatedChallengesCannotAuthorizeCcRelease(string prefix, string mode)
    {
        using var store = Store();
        var token = prefix + Guid.NewGuid().ToString("N");
        var entry = Entry(); entry.Mode = mode;
        await store.PutAsync(token, entry, _clock.GetUtcNow(), 300000, default);
        Assert.NotNull(await store.TakeAsync(token, default, "node-a"));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        Assert.False(await store.WasConsumedAsync(digest, "node-a", entry.ClientIp, default));
    }

    [Fact]
    public async Task ConcurrentConsumePersistsFenceAndSequenceSurvivesRestart()
    {
        var token = Token(); long sequence;
        using (var store = Store())
        {
            await store.PutAsync(token, Entry(), _clock.GetUtcNow(), 300000, default);
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => store.TakeAsync(token, default)));
            Assert.Single(results, r => r is not null);
            sequence = store.Sequence;
            Assert.Equal(2, sequence);
            await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(token, Entry(), _clock.GetUtcNow(), 300000, default));
        }
        using (var restarted = Store())
        {
            Assert.Null(await restarted.TakeAsync(token, default));
            await restarted.PutAsync(Token(), Entry(), _clock.GetUtcNow(), 300000, default);
            Assert.Equal(sequence + 1, restarted.Sequence);
        }
        using var record = System.Text.Json.JsonDocument.Parse(Directory.GetFiles(_directory, "*.json")
            .Select(File.ReadAllText).Single(text => text.Contains(token)));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, record.RootElement.GetProperty("Entry").ValueKind);
    }

    [Fact]
    public async Task RetainedFencesBoundCapacityAndExpireWithoutRevivingOldIssuance()
    {
        using var store = Store(1);
        var issued = _clock.GetUtcNow(); var token = Token();
        await store.PutAsync(token, Entry(), issued, 1000, default);
        Assert.NotNull(await store.TakeAsync(token, default));
        await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => store.PutAsync(Token(), Entry(), issued, 1000, default));
        _clock.Now = _clock.Now.AddSeconds(2);
        Assert.Null(await store.TakeAsync(token, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(token, Entry(), issued, 1000, default));
        await store.PutAsync(Token(), Entry(), _clock.GetUtcNow(), 1000, default);
    }

    [Fact]
    public async Task ExclusiveOwnerAndCorruptSequenceFailClosed()
    {
        using (var owner = Store())
        {
            await owner.OpenAsync(default);
            using var other = Store();
            await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => other.OpenAsync(default));
        }
        File.WriteAllText(Path.Combine(_directory, "sequence"), "invalid");
        using var corrupt = Store();
        await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => corrupt.OpenAsync(default));
    }

    [Fact]
    public async Task SequenceGapAndUncommittedTempAreRecoverableButMissingSequenceIsNot()
    {
        var token = Token();
        using (var store = Store()) await store.PutAsync(token, Entry(), _clock.GetUtcNow(), 300000, default);
        File.WriteAllText(Path.Combine(_directory, "sequence"), "{\"ClusterId\":\"test\",\"AuthorityNodeId\":\"authority\",\"Sequence\":10}");
        var interrupted = Path.Combine(_directory, new string('A', 64) + ".json.tmp");
        File.WriteAllText(interrupted, "uncommitted-answer");
        using (var recovered = Store())
        {
            Assert.NotNull(await recovered.TakeAsync(token, default));
            Assert.Equal(11, recovered.Sequence);
            Assert.False(File.Exists(interrupted));
        }
        File.Delete(Path.Combine(_directory, "sequence"));
        using var missing = Store();
        await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => missing.OpenAsync(default));
    }
    [Fact]
    public async Task CancellationDoesNotConsumeBeforeAdmission()
    {
        using var store = Store(); var token = Token();
        await store.PutAsync(token, Entry(), _clock.GetUtcNow(), 300000, default);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TakeAsync(token, stop.Token));
        Assert.NotNull(await store.TakeAsync(token, default));
    }
    [Theory]
    [InlineData("unrelated.tmp")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.json.tmp")]
    public async Task UnrelatedTemporaryFileIsPreservedAndDirectoryRejected(string name)
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, name);
        File.WriteAllText(file, "user-owned-content");
        using var store = Store();
        await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => store.OpenAsync(default));
        Assert.Equal("user-owned-content", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(_directory, "sequence")));
    }
    [Fact]
    public async Task ByteCapacityLeavesRoomForConsumptionAndRestart()
    {
        var options = Options(); options.MaxBytes = 16384; var token = Token();
        using (var store = new FileCaptchaAuthority(Microsoft.Extensions.Options.Options.Create(options), _clock))
        {
            var entry = Entry(); entry.Answer = new string('x', 3500);
            await store.PutAsync(token, entry, _clock.GetUtcNow(), 300000, default);
            await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => store.PutAsync(Token(), entry, _clock.GetUtcNow(), 300000, default));
            Assert.NotNull(await store.TakeAsync(token, default));
        }
        using var restarted = new FileCaptchaAuthority(Microsoft.Extensions.Options.Options.Create(options), _clock);
        Assert.Null(await restarted.TakeAsync(token, default));
    }
    [Fact]
    public async Task InterruptedConsumeDoesNotReturnAnswerAndRequiresRecovery()
    {
        var token = Token();
        using (var store = Store())
        {
            await store.PutAsync(token, Entry(), _clock.GetUtcNow(), 300000, default);
            var recordPath = Directory.GetFiles(_directory, "*.json").Single();
            Directory.CreateDirectory(recordPath + ".tmp");
            await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => store.TakeAsync(token, default));
            await Assert.ThrowsAsync<CaptchaStoreUnavailableException>(() => store.TakeAsync(token, default));
            Directory.Delete(recordPath + ".tmp");
        }
        using var recovered = Store();
        Assert.NotNull(await recovered.TakeAsync(token, default));
        Assert.Null(await recovered.TakeAsync(token, default));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
