using LyWaf.Control;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Lifecycle;
using LyWaf.Services.ManagementTasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class UpgradeActivationTaskTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingVersionFailsInQueueWithoutStoppingTheHost(bool rollback)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-upgrade-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var jobs = new ManagementJobService();
        try
        {
            var upgrades = new UpgradePackageService(Options.Create(new UpgradeOptions
            {
                InboxDirectory = Path.Combine(root, "inbox"), ReleaseDirectory = Path.Combine(root, "releases")
            }));
            var lifetime = new Lifetime();
            var drain = new GracefulShutdownCoordinator();
            var audit = new Audit();
            var context = new DefaultHttpContext();
            context.Items["Username"] = "operator";
            var result = ControlApi.QueueUpgradeHandoff(rollback ? null : "missing", rollback, context,
                upgrades, jobs, drain, lifetime, new ConfigurationBuilder().Build(), audit);
            Assert.Equal(202, ((IStatusCodeHttpResult)result).StatusCode);
            var response = Assert.IsType<ManagementJobResponse>(((IValueHttpResult)result).Value);
            Assert.Equal(ManagementJobState.Queued, response.Data.State);
            Assert.Contains(response.Data.Id, Assert.Single(audit.Entries));
            await jobs.StartAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (jobs.Find(response.Data.Id)!.FinishedAt == null) await Task.Delay(10, timeout.Token);
            Assert.Equal(ManagementJobState.Failed, jobs.Find(response.Data.Id)!.State);
            Assert.False(lifetime.Stopped);
            Assert.False(drain.Snapshot().Draining);
        }
        finally { await jobs.StopAsync(default); Directory.Delete(root, true); }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => Stopped = true;
    }

    private sealed class Audit : IAuditLogService
    {
        public List<string> Entries { get; } = [];
        public void Log(string username, string action, string ip) => Entries.Add(action);
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }
}
