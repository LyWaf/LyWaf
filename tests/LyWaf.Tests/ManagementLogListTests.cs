using LyWaf.Services.ManagementApi;
using LyWaf.Utils;
using LyWaf.Control;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementLogListTests
{
    [Theory]
    [InlineData("../pcap_test.log")]
    [InlineData("pcap_test.log:secret")]
    [InlineData("intercept_test.log")]
    [InlineData("pcap_test.txt")]
    [InlineData("pcap_dir/test.log")]
    [InlineData("pcap_dir\\test.log")]
    public void LogFamilyRejectsTraversalAlternateStreamsAndUnrelatedFiles(string file)
    {
        var error = Assert.Throws<ArgumentException>(() => ControlApi.ResolveRequestLogPath(file, "pcap_"));
        Assert.Equal("file", error.ParamName);
    }

    [Theory]
    [InlineData("asc", "/first")]
    [InlineData("desc", "/last")]
    public async Task SortsOutOfOrderTimestampsAndCountsBeforePaging(string direction, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Entry("03", "/last") + Entry("01", "/first") + Entry("02", "/middle"));
            using var scanner = new ReverseLogScanner(path);
            var query = new ManagementListQuery(1, 1, "time", direction, "GET", null, null);
            var result = await scanner.QueryAsync(query);
            Assert.Equal(3, result.Total);
            Assert.Equal(expected, Assert.Single(result.Data).Url);
            var empty = await scanner.QueryAsync(query with { Page = int.MaxValue });
            Assert.Empty(empty.Data);
            Assert.Equal(3, empty.Total);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TimeAndLiteralSearchFiltersApplyTogether()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Entry("03", "/match") + Entry("01", "/match") + Entry("02", "/other"));
            using var scanner = new ReverseLogScanner(path);
            var result = await scanner.QueryAsync(new ManagementListQuery(1, 20, "time", "asc", "MATCH",
                DateTimeOffset.Parse("2026-09-09T00:00:02Z"), DateTimeOffset.Parse("2026-09-09T00:00:03Z")));
            Assert.Equal(1, result.Total);
            Assert.Equal("/match", Assert.Single(result.Data).Url);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RejectsOversizedEntryInsteadOfAllocatingItsFullBody()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Entry("01", "/large"));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.SetLength(9 * 1024 * 1024);
            using var scanner = new ReverseLogScanner(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => scanner.QueryAsync(new ManagementListQuery(1, 20, "time", "desc", "", null, null)));
        }
        finally { File.Delete(path); }
    }

    private static string Entry(string seconds, string path) =>
        $"========== [2026-09-09T00:00:{seconds}Z] ==========\nGET {path} HTTP/1.1\nHost: localhost\n\n";

    [Fact]
    public async Task CancelledRequestDoesNotScanTheFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var scanner = new ReverseLogScanner(path);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.QueryAsync(
                new ManagementListQuery(1, 20, "time", "desc", "", null, null), new CancellationToken(true)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SnapshotIgnoresEntriesAppendedAfterOpening()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Entry("01", "/first"));
            using var scanner = new ReverseLogScanner(path);
            await File.AppendAllTextAsync(path, Entry("02", "/later"));
            var result = await scanner.QueryAsync(new ManagementListQuery(1, 20, "time", "desc", "", null, null));
            Assert.Equal(1, result.Total);
            Assert.Equal("/first", Assert.Single(result.Data).Url);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OversizedPageFailsWithoutSilentlyTruncatingResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                for (var i = 0; i < 3; i++)
                {
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Entry("01", "/large")));
                    stream.SetLength(stream.Position + 6 * 1024 * 1024);
                    stream.Position = stream.Length - 1;
                    stream.WriteByte((byte)'\n');
                }
            using var scanner = new ReverseLogScanner(path);
            var error = await Assert.ThrowsAsync<ArgumentException>(() => scanner.QueryAsync(
                new ManagementListQuery(1, 20, "time", "desc", "", null, null)));
            Assert.Equal("pageSize", error.ParamName);
        }
        finally { File.Delete(path); }
    }
}
