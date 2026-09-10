using DuckDB.NET.Data;
using LyWaf.Services.Backup;
using Xunit;

namespace LyWaf.Tests;

public sealed class DuckDbBackupSnapshotTests
{
    [Fact]
    public async Task SnapshotIncludesCommittedDataAndSchemaWithoutUncommittedWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.duckdb");
            var target = Path.Combine(directory, "snapshot.duckdb");
            using (var connection = new DuckDBConnection($"Data Source={source}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE requests(id INTEGER PRIMARY KEY, count INTEGER); INSERT INTO requests VALUES (1, 5); BEGIN; INSERT INTO requests VALUES (2, 10)";
                command.ExecuteNonQuery();
                await DuckDbBackupSnapshot.CreateAsync(source, target, CancellationToken.None);
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
            }
            Assert.False(File.Exists(target + ".wal"));
            using var snapshot = new DuckDBConnection($"Data Source={target}");
            snapshot.Open();
            using var read = snapshot.CreateCommand();
            read.CommandText = "SELECT CAST(sum(count) AS INTEGER) FROM requests";
            Assert.Equal(5, Convert.ToInt32(read.ExecuteScalar()));
            read.CommandText = "INSERT INTO requests VALUES (1, 8)";
            Assert.ThrowsAny<Exception>(() => read.ExecuteNonQuery());
        }
        finally { Directory.Delete(directory, true); }
    }
}
