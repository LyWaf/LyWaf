using LyWaf.Services.Backup;
using Xunit;

namespace LyWaf.Tests;

public sealed class BackupArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lywaf-backup-test-" + Guid.NewGuid().ToString("N"));
    private const string Password = "backup-test-password";
    public BackupArchiveTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task PasswordlessArchiveSupportsPreflightRestoreAndRollback()
    {
        var source = Path.Combine(_root, "plain-source");
        var target = Path.Combine(_root, "plain-target");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(target, "old");
        var service = new BackupArchive();
        using var archive = new MemoryStream();
        await service.CreateAsync(new Dictionary<string, string> { ["config"] = source }, archive, "", default);
        Assert.Equal("LYWAFBP1", System.Text.Encoding.ASCII.GetString(archive.ToArray(), 0, 8));
        var targets = new Dictionary<string, string> { ["config"] = target };
        archive.Position = 0;
        await service.RestoreAsync(archive, "", targets, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, true, default);
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        archive.Position = 0;
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(archive, "", targets,
            (_, _) => Task.CompletedTask, _ => ++attempts == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask, false, default));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        archive.Position = 0;
        await service.RestoreAsync(archive, "", targets, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, false, default);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task PreflightDoesNotModifyAndRestoreRollsBackActivationFailure()
    {
        var service = new BackupArchive();
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        await File.WriteAllTextAsync(source, "new-config");
        await File.WriteAllTextAsync(target, "old-config");
        using var archive = new MemoryStream();
        await service.CreateAsync(new Dictionary<string, string> { ["config.ly"] = source }, archive, Password, default);
        var targets = new Dictionary<string, string> { ["config.ly"] = target };
        async Task Validate(IReadOnlyDictionary<string, string> files, CancellationToken token)
            => Assert.Equal("new-config", await File.ReadAllTextAsync(files["config.ly"], token));
        archive.Position = 0;
        var manifest = await service.RestoreAsync(archive, Password, targets, Validate, _ => Task.CompletedTask, true, default);
        Assert.Single(manifest.Files);
        Assert.Equal("old-config", await File.ReadAllTextAsync(target));
        archive.Position = 0;
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(archive, Password, targets,
            Validate, _ => ++attempts == 1 ? Task.FromException(new InvalidOperationException("activation failed")) : Task.CompletedTask, false, default));
        Assert.Equal(2, attempts);
        Assert.Equal("old-config", await File.ReadAllTextAsync(target));
        archive.Position = 0;
        await service.RestoreAsync(archive, Password, targets, Validate, _ => Task.CompletedTask, false, default);
        Assert.Equal("new-config", await File.ReadAllTextAsync(target));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task UnknownFileAndFailedValidationNeverWriteTargets()
    {
        var service = new BackupArchive();
        var file = Path.Combine(_root, "config");
        await File.WriteAllTextAsync(file, "before");
        using var archive = new MemoryStream();
        await service.CreateAsync(new Dictionary<string, string> { ["config.ly"] = file }, archive, Password, default);
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(archive, Password,
            new Dictionary<string, string>(), (_, _) => Task.CompletedTask, _ => Task.CompletedTask, false, default));
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(archive, Password,
            new Dictionary<string, string> { ["config.ly"] = file }, (_, _) => throw new InvalidDataException("bad configuration"),
            _ => Task.CompletedTask, false, default));
        Assert.Equal("before", await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData("../config.ly")]
    [InlineData("C:\\config.ly")]
    [InlineData("Manifest.json")]
    public async Task RejectsUntrustedArchiveNames(string name)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupArchive().CreateAsync(
            new Dictionary<string, string> { [name] = "unused" }, new MemoryStream(), Password, default));
    }

    [Fact]
    public async Task RemovedFilesAreRestoredWhenActivationFails()
    {
        var source = Path.Combine(_root, "source");
        var extra = Path.Combine(_root, "extra");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(extra, "old-extra");
        using var archive = new MemoryStream();
        var service = new BackupArchive();
        await service.CreateAsync(new Dictionary<string, string> { ["source"] = source }, archive, Password, default);
        archive.Position = 0;
        var attempt = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(archive, Password,
            new Dictionary<string, string> { ["source"] = source, ["extra"] = extra }, (_, _) => Task.CompletedTask,
            _ => ++attempt == 1 ? Task.FromException(new InvalidOperationException("failed")) : Task.CompletedTask,
            false, default, removeMissing: true));
        Assert.Equal("old-extra", await File.ReadAllTextAsync(extra));
    }
}
