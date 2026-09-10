using LyWaf.Services.Backup;
using LyWaf.Services.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class BackupConfigurationCandidateTests
{
    [Fact]
    public void RejectsInvalidListenerBeforeRestore()
    {
        var candidate = BackupConfigurationCandidate.Build("config.yaml", "WafInfos:\n  Listens:\n    - Host: 127.0.0.1\n      Port: -1\n", null);
        try { Assert.ThrowsAny<Exception>(() => new ConfigurationLifecycleService().Validate(candidate)); }
        finally { (candidate as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task LoadsOnlyStagedPatchWithoutChangingGlobalConfigPath()
    {
        var patch = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(patch, "{\"Routes\":{\"site\":{\"Match\":{\"Path\":\"/new\"}}}}");
            var original = LyWaf.Shared.SharedData.ConfigFilePath;
            var candidate = BackupConfigurationCandidate.Build("config.yaml", "ReverseProxy:\n  Routes:\n    site:\n      Match:\n        Path: /old\n", patch);
            try
            {
                Assert.Equal("/new", candidate["ReverseProxy:Routes:site:Match:Path"]);
                Assert.Equal(original, LyWaf.Shared.SharedData.ConfigFilePath);
            }
            finally { (candidate as IDisposable)?.Dispose(); }
        }
        finally { File.Delete(patch); }
    }
}
