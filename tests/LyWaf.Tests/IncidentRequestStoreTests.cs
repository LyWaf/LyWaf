using LyWaf.Services.Incidents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class IncidentRequestStoreTests
{
    [Fact]
    public async Task StoresRequestsBeyondSummarySamplesAndPagesWithoutDuplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-requests-test-" + Guid.NewGuid().ToString("N"));
        using var store = new IncidentRequestStore(NullLogger<IncidentRequestStore>.Instance, directory);
        await store.StartAsync(default);
        for (var i = 0; i < 25; i++) store.Record(Incident(i));
        await store.StopAsync(default);
        try
        {
            var first = await store.ReadAsync("incident", null, 20, default);
            Assert.Equal(25, first.Total);
            Assert.Equal(20, first.Data.Length);
            Assert.NotNull(first.NextCursor);
            var second = await store.ReadAsync("incident", first.NextCursor, 20, default);
            Assert.Equal(5, second.Data.Length);
            Assert.Null(second.NextCursor);
            Assert.Equal(25, first.Data.Concat(second.Data).Select(x => x.Id).Distinct().Count());
            Assert.Empty((await store.ReadAsync("other", null, 20, default)).Data);
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("incident", "bad-cursor", 20, default));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task QueueIsBoundedAndReportsDroppedRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-requests-test-" + Guid.NewGuid().ToString("N"));
        using var store = new IncidentRequestStore(NullLogger<IncidentRequestStore>.Instance, directory);
        for (var i = 0; i < 520; i++) store.Record(Incident(i));
        Assert.Equal(8, (await store.ReadAsync("incident", null, 20, default)).Dropped);
    }

    private static SecurityIncident Incident(int i)
    {
        var time = DateTimeOffset.UtcNow;
        return new("incident", "site", "localhost", "127.0.0.1", "rule", "SQL", i + 1, time, time,
            IncidentDisposition.Pending, 0, [new("request-" + i, time, "GET", "/test", "", ["q"], ["query"], ["url-decode"], false)], []);
    }
}
