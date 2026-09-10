using DuckDB.NET.Data;
using LyWaf.Services.DuckDb;
using Xunit;

namespace LyWaf.Tests;

public sealed class HistoryDatabaseBoundsTests
{
    private sealed class Database : IDuckDbService
    {
        private readonly DuckDBConnection connection = new("Data Source=:memory:");
        public Database() => connection.Open();
        public bool IsEnabled { get; set; } = true;
        public DuckDBConnection GetConnection() => connection;
        public Task InitializeSchemaAsync() => Task.CompletedTask;
        public void Checkpoint() { }
        public void Dispose() => connection.Dispose();
        public void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void OversizedChartFailsInsteadOfReturningPartialHistory()
    {
        using var database = new Database();
        database.Execute("CREATE TABLE qps_snapshots AS SELECT TIMESTAMP '2026-01-01' + range * INTERVAL '1 minute' AS snapshot_time, 1::BIGINT AS request_count FROM range(20001)");
        var service = new DuckDbQueryService(database);
        var error = Assert.Throws<ArgumentException>(() => service.GetQpsHistory(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)));
        Assert.Equal("from", error.ParamName);
        Assert.Equal(20000, service.GetQpsHistory(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1).AddMinutes(19999)).Count);
    }

    [Fact]
    public void QueryFailureCannotMasqueradeAsAnEmptyHistory()
    {
        using var database = new Database();
        Assert.ThrowsAny<Exception>(() => new DuckDbQueryService(database).GetQpsHistory(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
    }

    [Fact]
    public void TimingPathFilterTreatsPercentAsLiteral()
    {
        using var database = new Database();
        database.Execute("CREATE TABLE api_timing_snapshots AS SELECT TIMESTAMP '2026-01-01' AS snapshot_time, path, 'GET' AS method, '' AS backend, '' AS original_host, 1::INTEGER AS request_count, 1::BIGINT AS total_time, 1::BIGINT AS backend_time, 1::BIGINT AS min_total_time, 1::BIGINT AS max_total_time, 1::BIGINT AS min_backend_time, 1::BIGINT AS max_backend_time, '{}' AS status_codes FROM (VALUES ('/literal%'), ('/other')) AS paths(path)");
        var result = new DuckDbQueryService(database).GetApiTimingHistory(new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), "%");
        Assert.Equal("/literal%", Assert.Single(result).Path);
    }
}
