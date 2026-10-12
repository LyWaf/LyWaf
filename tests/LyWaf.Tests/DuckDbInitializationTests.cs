using LyWaf.Services.DuckDb;
using LyWaf.Services.Metrics;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class DuckDbInitializationTests
{
    [Fact]
    public async Task ConcurrentInitializationWorksForNewAndExistingDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-schema-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var options = Options.Create(new DuckDbOptions
            {
                Enabled = true,
                DatabasePath = Path.Combine(directory, "stats.duckdb")
            });
            for (var run = 0; run < 2; run++)
            {
                using var database = new DuckDbService(options,
                    new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())));
                await Task.WhenAll(Enumerable.Range(0, 16)
                    .Select(_ => Task.Run(() => database.InitializeSchemaAsync())));
                Assert.True(database.IsEnabled);
                using var command = database.GetConnection().CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM qps_snapshots";
                Assert.Equal((long)run, Convert.ToInt64(command.ExecuteScalar()));
                command.CommandText = "INSERT INTO qps_snapshots VALUES (CURRENT_TIMESTAMP, 1)";
                command.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
