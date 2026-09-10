using LyWaf.Control;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Backup;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementTasks;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class ConfigurationTaskTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PublishValidatesAndRestoresFailedCandidate(bool existingDraft, bool valid)
    {
        await Exercise(existingDraft, valid, false);
    }

    [Fact]
    public async Task HistoricalRollbackAlsoValidatesBeforeAcceptingConfiguration()
        => await Exercise(true, false, true);

    private static async Task Exercise(bool existingDraft, bool valid, bool rollback)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-config-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var original = SharedData.ConfigFilePath;
        var config = Path.Combine(directory, "config.yaml");
        var draft = Path.Combine(directory, ".lywaf.draft.yaml");
        using var jobs = new ManagementJobService();
        try
        {
            SharedData.ConfigFilePath = config;
            await File.WriteAllTextAsync(config, Content(8080));
            var versions = new ConfigVersionService(new HttpContextAccessor());
            string? rollbackId = null;
            if (rollback) rollbackId = (await versions.WriteDraftAsync(Content(-1), "fixture")).Id;
            if (existingDraft) await versions.WriteDraftAsync(Content(8080), "fixture");
            using var root = new ConfigurationRoot([new FileProvider(config, draft)]);
            using var services = new ServiceCollection().AddSingleton(jobs).AddSingleton(versions)
                .AddSingleton<ConfigurationLifecycleService>().AddSingleton<IAuditLogService, Audit>().BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Items["Username"] = "config-operator";
            var result = ControlApi.QueueConfigurationChange(context, root, rollback ? null : Content(valid ? 8081 : -1), rollbackId);
            Assert.Equal(202, ((IStatusCodeHttpResult)result).StatusCode);
            var response = Assert.IsType<ManagementJobResponse>(((IValueHttpResult)result).Value);
            Assert.Equal(ManagementJobState.Queued, response.Data.State);
            await jobs.StartAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(response.Data.Id)!.FinishedAt == null) await Task.Delay(10, timeout.Token);
            Assert.Equal(valid ? ManagementJobState.Succeeded : ManagementJobState.Failed, jobs.Find(response.Data.Id)!.State);
            Assert.Equal(valid ? "8081" : "8080", root["WafInfos:Listens:0:Port"]);
            Assert.Equal(existingDraft || valid, File.Exists(draft));
            if (File.Exists(draft)) Assert.Equal(Content(valid ? 8081 : 8080), await File.ReadAllTextAsync(draft));
            var history = await versions.GetVersionsAsync();
            Assert.Equal("config-operator", history[0].Operator);
        }
        finally
        {
            await jobs.StopAsync(default);
            SharedData.ConfigFilePath = original;
            Directory.Delete(directory, true);
        }
    }

    private static string Content(int port) => $"WafInfos:\n  Listens:\n    - Host: localhost\n      Port: {port}\n";

    private sealed class FileProvider(string config, string draft) : ConfigurationProvider
    {
        public override void Load()
        {
            var candidate = BackupConfigurationCandidate.Build(config, File.ReadAllText(File.Exists(draft) ? draft : config), null);
            try { Data = candidate.AsEnumerable().ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase); }
            finally { (candidate as IDisposable)?.Dispose(); }
        }
    }

    private sealed class Audit : IAuditLogService
    {
        public void Log(string username, string action, string ip) { }
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }
}
