using LyWaf.Config;
using LyWaf.Control.Infrastructure;
using LyWaf.Services.Configuration;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class ConfigurationPatchRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidPatchRestoresDiskEvenAfterRequestCancellation(bool existing, bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-patch-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = SharedData.ConfigFilePath;
        using var cancellation = new CancellationTokenSource();
        try
        {
            SharedData.ConfigFilePath = Path.Combine(directory, "config.yaml");
            var path = LbPatchConfig.GetPatchFilePath();
            var versions = new ConfigVersionService(new HttpContextAccessor());
            const string original = "{\"WafInfos\":{\"Listens\":[{\"Port\":8080}]}}";
            if (existing) await versions.WritePatchAsync(path, original, "fixture");
            using var root = new ConfigurationRoot([new PatchProvider(path, cancel ? cancellation : null)]);
            var patches = new ControlConfigurationPatchService(root, versions, new ConfigurationLifecycleService());
            await Assert.ThrowsAsync<InvalidDataException>(() => patches.SetSectionAsync("WafInfos",
                new { Listens = new[] { new { Port = -1 } } }, "invalid", cancellation.Token));
            Assert.Equal(existing, File.Exists(path));
            if (existing) Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal(existing ? "8080" : null, root["WafInfos:Listens:0:Port"]);
            if (!existing) Assert.Empty(root.GetSection("WafInfos:Listens").GetChildren());
            new ConfigurationLifecycleService().Validate(root);
            Assert.Equal(cancel, cancellation.IsCancellationRequested);
        }
        finally
        {
            SharedData.ConfigFilePath = previous;
            Directory.Delete(directory, true);
        }
    }

    private sealed class PatchProvider(string path, CancellationTokenSource? cancellation) : ConfigurationProvider, IRestorableConfigurationProvider
    {
        public void RestoreValues(IReadOnlyDictionary<string, string?> values) =>
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        public override void Load()
        {
            if (!File.Exists(path)) { Data.Clear(); return; }
            using var stream = File.OpenRead(path);
            using var parsed = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(stream).Build();
            Data = parsed.AsEnumerable().ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            if (Data.TryGetValue("WafInfos:Listens:0:Port", out var port) && port == "-1") cancellation?.Cancel();
        }
    }
}
