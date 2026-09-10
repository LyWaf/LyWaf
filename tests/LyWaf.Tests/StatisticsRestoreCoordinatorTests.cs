using DuckDB.NET.Data;
using LyWaf.Services.Backup;
using LyWaf.Services.DuckDb;
using Xunit;

namespace LyWaf.Tests;

public sealed class StatisticsRestoreCoordinatorTests
{
    [Fact]
    public async Task PreparedRestoreAppliesBeforeStartupAndKeepsPasswordProtected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-stat-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.duckdb");
            var target = Path.Combine(directory, "target.duckdb");
            CreateDatabase(source, 5);
            CreateDatabase(target, 99);
            var archive = Path.Combine(directory, "backup.lybackup");
            const string password = "statistics-password-2026";
            await using (var output = File.Create(archive))
                await new BackupArchive().CreateAsync(new Dictionary<string, string> { ["statistics"] = source }, output, password, CancellationToken.None);
            await StatisticsRestoreCoordinator.PrepareAsync(directory, target, archive, password, "operator", CancellationToken.None);
            Assert.DoesNotContain(password, File.ReadAllText(Path.Combine(directory, ".lywaf.statistics-restore")));
            Assert.Equal(99L, ReadCount(target));
            using var lease = StatisticsRestoreCoordinator.AcquireLease(target, true);
            StatisticsRestoreCoordinator.ApplyBeforeStartup(directory, target);
            Assert.False(StatisticsRestoreCoordinator.IsPending(directory));
            Assert.Equal(5L, ReadCount(target));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChangedDestinationIsRejectedWithoutApplyingOrRemovingPendingRestore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-stat-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "backup.lybackup");
            await File.WriteAllTextAsync(archive, "fixture");
            await StatisticsRestoreCoordinator.PrepareAsync(directory, Path.Combine(directory, "old.duckdb"), archive, "statistics-password-2026", "operator", CancellationToken.None);
            Assert.Throws<InvalidDataException>(() => StatisticsRestoreCoordinator.ApplyBeforeStartup(directory, Path.Combine(directory, "new.duckdb")));
            Assert.True(StatisticsRestoreCoordinator.IsPending(directory));
            StatisticsRestoreCoordinator.Cancel(directory);
            Assert.False(StatisticsRestoreCoordinator.IsPending(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DatabaseLeaseRejectsConcurrentInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-stat-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "stats.duckdb");
            using var lease = StatisticsRestoreCoordinator.AcquireLease(path, true);
            Assert.Throws<IOException>(() => StatisticsRestoreCoordinator.AcquireLease(path, true));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PreflightRejectsViewSubstitutedForRequiredTable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-stat-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "stats.duckdb");
            CreateDatabase(path, 5);
            using (var connection = new DuckDBConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE traffic_snapshots RENAME TO original_snapshots; CREATE VIEW traffic_snapshots AS SELECT * FROM original_snapshots";
                command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => StatisticsRestoreCoordinator.ValidateAsync(
                new Dictionary<string, string> { ["statistics"] = path }, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CreateDatabase(string path, long count)
    {
        using var connection = new DuckDBConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        foreach (var sql in DuckDbSchema.CreateTableStatements) { command.CommandText = sql; command.ExecuteNonQuery(); }
        command.CommandText = $"INSERT INTO traffic_snapshots(snapshot_time, total_requests) VALUES (current_timestamp, {count})";
        command.ExecuteNonQuery();
    }

    private static long ReadCount(string path)
    {
        using var connection = new DuckDBConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT total_requests FROM traffic_snapshots";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
