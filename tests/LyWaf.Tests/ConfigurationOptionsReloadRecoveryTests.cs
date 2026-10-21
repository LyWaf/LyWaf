using System.Text.Json;
using LyWaf.Config;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class ConfigurationOptionsReloadRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "reload-options-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "protocol.yaml");
    private string BasePath => Path.Combine(_directory, "base.yaml");
    public ConfigurationOptionsReloadRecoveryTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(BasePath, "Probe:\n  Value: original\n");
        Write(false, 'a');
    }

    private void Write(bool secondWriter, char key)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new
        {
            NodeCommunication = new
            {
                Enabled = true, Role = "Node", ClusterId = "cluster", NodeId = "edge", StateDirectory = _directory,
                Peers = new Dictionary<string, object>
                {
                    ["primary"] = new { Role = "Controller", AdmissionState = "Active", Keys = new { key = new string(key, 32) }, AllowedOperations = new[] { "node.status", "configuration.commit" } },
                    ["secondary"] = new { Role = "Controller", AdmissionState = "Active", Keys = new { key = new string('b', 32) }, AllowedOperations = secondWriter ? new[] { "node.status", "configuration.commit" } : new[] { "node.status" } }
                }
            }
        }));
    }

    [Fact]
    public void RealFileReloadRejectsSecondWriterThenRestoresCachesAndAcceptsValidReload()
    {
        using var root = new ConfigurationManager();
        root.AddYamlFile(BasePath, false, false).AddDraftAwareYamlFile(FilePath, false);
        using var services = new ServiceCollection().AddSingleton<ConfigurationLifecycleService>()
            .AddLyWafDistribution(root).BuildServiceProvider();
        var lifecycle = services.GetRequiredService<ConfigurationLifecycleService>();
        var communication = services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>();
        var distribution = services.GetRequiredService<IOptionsMonitor<DistributionOptions>>();
        var runtime = services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>();
        Assert.Equal("edge", distribution.CurrentValue.NodeId);
        Assert.Equal("edge", runtime.CurrentValue.NodeId);
        var observations = new List<NodeCommunicationOptions>();
        using var subscription = communication.OnChange(value => observations.Add(value));
        var snapshot = lifecycle.Capture(root);
        Write(true, 'x');
        var failure = Assert.ThrowsAny<Exception>(() => lifecycle.ReloadAndValidate(root, snapshot));
        Assert.Contains("Only one admitted configuration writer", failure.ToString());
        Assert.True(NodeAdmissionPolicy.HasSingleConfigurationWriter(communication.CurrentValue));
        Assert.DoesNotContain("configuration.commit", communication.CurrentValue.Peers["secondary"].AllowedOperations);
        Assert.Equal(new string('a', 32), communication.CurrentValue.Peers["primary"].Keys["key"]);
        Assert.Equal("edge", distribution.CurrentValue.NodeId);
        Assert.Equal("edge", runtime.CurrentValue.NodeId);
        Assert.NotEmpty(observations);
        Assert.All(observations, value => Assert.True(NodeAdmissionPolicy.HasSingleConfigurationWriter(value)));

        Write(false, 'c');
        File.WriteAllText(BasePath, "Probe:\n  Value: changed\n");
        lifecycle.ReloadAndValidate(root, lifecycle.Capture(root));
        Assert.Equal(new string('c', 32), communication.CurrentValue.Peers["primary"].Keys["key"]);
        Assert.Equal("changed", root["Probe:Value"]); // No restored value polluted the higher-priority provider.
        Assert.False(((IConfigurationRoot)root).Providers.Last().TryGet("Probe:Value", out _));
        Assert.True(NodeAdmissionPolicy.HasSingleConfigurationWriter(communication.CurrentValue));
    }

    [Fact]
    public void InvalidStartupStillFailsAndRepeatedRejectedReloadDoesNotRelaxValidation()
    {
        Write(true, 'x');
        using var root = (ConfigurationRoot)new ConfigurationBuilder().AddDraftAwareYamlFile(FilePath, false).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(root).BuildServiceProvider();
        var monitor = services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>();
        Assert.Throws<OptionsValidationException>(() => monitor.CurrentValue);
        Assert.Throws<OptionsValidationException>(() => monitor.CurrentValue);
    }

    [Fact]
    public void ValidationAfterNotificationRestoresSubscriberAndProviderValues()
    {
        using var root = new ConfigurationManager();
        root.AddYamlFile(BasePath, false, false).AddDraftAwareYamlFile(FilePath, false);
        var collection = new ServiceCollection().AddSingleton<ConfigurationLifecycleService>().AddLyWafDistribution(root);
        collection.Configure<ProbeOptions>(root.GetSection("Probe"));
        using var services = collection.BuildServiceProvider();
        var lifecycle = services.GetRequiredService<ConfigurationLifecycleService>();
        var monitor = services.GetRequiredService<IOptionsMonitor<ProbeOptions>>();
        var observed = new List<string>();
        using var subscription = monitor.OnChange(value => observed.Add(value.Value));
        Assert.Equal("original", monitor.CurrentValue.Value);
        var before = lifecycle.Capture(root);
        File.WriteAllText(BasePath, "Probe:\n  Value: invalid-candidate\nWafInfos:\n  Listens:\n    - Port: 70000\n");
        Assert.Throws<InvalidDataException>(() => lifecycle.ReloadAndValidate(root, before));
        Assert.Equal("original", monitor.CurrentValue.Value);
        Assert.Equal("original", observed.Last());
        Assert.Empty(root.GetSection("WafInfos:Listens").GetChildren());
        lifecycle.Validate(root);
        File.WriteAllText(BasePath, "Probe:\n  Value: recovered\n");
        lifecycle.ReloadAndValidate(root, lifecycle.Capture(root));
        Assert.Equal("recovered", monitor.CurrentValue.Value);
    }

    public sealed class ProbeOptions { public string Value { get; set; } = ""; }
    public void Dispose() => Directory.Delete(_directory, true);
}
