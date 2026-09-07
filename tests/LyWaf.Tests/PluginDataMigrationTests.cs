using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginDataMigrationTests
{
    [Fact]
    public async Task AppliesCompleteMigrationChainAndPersistsTargetVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new PluginOptions { Permissions = { ["migration-test"] = PluginPermission.DataDirectory } };
            var manager = new PluginManager(new ConfigurationBuilder().Build(), new PluginEventBus(),
                new StaticOptionsMonitor<PluginOptions>(options));
            var context = new TestContext(new PluginStorage(root, true));
            await manager.ApplyDataMigrationsAsync(new MigratingPlugin(), context, default);

            Assert.True(context.Storage.Exists("migrated.txt"));
            using var version = context.Storage.OpenRead(".lywaf-data-version");
            Span<byte> bytes = stackalloc byte[4];
            Assert.Equal(4, version.Read(bytes));
            Assert.Equal(2, BitConverter.ToInt32(bytes));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class MigratingPlugin : LyWafPluginBase, IPluginDataMigrationProvider
    {
        public override PluginMetadata Metadata { get; } = new()
        {
            Id = "migration-test", Name = "Migration test", DataVersion = 2,
            RequestedPermissions = PluginPermission.DataDirectory
        };
        public IReadOnlyList<IPluginDataMigration> GetMigrations() =>
            [new Migration(0, 1), new Migration(1, 2, writeMarker: true)];
    }

    private sealed class Migration(int from, int to, bool writeMarker = false) : IPluginDataMigration
    {
        public int FromVersion => from;
        public int ToVersion => to;
        public Task MigrateAsync(IPluginStorage storage, CancellationToken cancellationToken)
        {
            if (writeMarker) using (storage.OpenWrite("migrated.txt")) { }
            return Task.CompletedTask;
        }
    }

    private sealed class TestContext(IPluginStorage storage) : IPluginContext
    {
        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        public NLog.Logger Logger { get; } = NLog.LogManager.GetCurrentClassLogger();
        public string DataDirectory => "";
        public IPluginTaskCoordinator Tasks => throw new NotSupportedException();
        public IPluginStorage Storage => storage;
        public T GetPluginConfig<T>(string? sectionName = null) where T : class, new() => new();
        public ILyWafPlugin? GetPlugin(string pluginId) => null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [];
        public Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : class => Task.CompletedTask;
        public IDisposable SubscribeEvent<TEvent>(Func<TEvent, Task> handler) where TEvent : class => new EmptyDisposable();
    }

    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
}
