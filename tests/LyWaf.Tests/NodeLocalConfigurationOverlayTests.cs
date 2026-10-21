using System.Security.Cryptography;
using System.Text;
using LyWaf.Config;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeLocalConfigurationOverlayTests
{
    private static string Content(bool ly, string identity, string value) => ly ? $$"""
        NodeCommunication {
            NodeId = "{{identity}}"
            ClusterId = "cluster"
            Peers {
                peer {
                    Keys { key = "{{identity}}-secret" }
                }
            }
        }
        Distribution {
            NodeId = "{{identity}}"
            Role = "{{(identity == "local" ? "Node" : "Controller")}}"
            StateDirectory = "{{identity}}-state"
            Nodes { edge { Endpoint = "https://{{identity}}" } }
        }
        control_listen = "127.0.0.1:7030"
        FileServer { Items { upload { Root = "{{identity}}-uploads" } } }
        Protect { CheckArgsFile = "{{identity}}-rules" }
        SpeedLimit { Value = "{{value}}" }
        """ : $$"""
        NodeCommunication:
          NodeId: {{identity}}
          ClusterId: cluster
          Peers:
            peer:
              Keys:
                key: {{identity}}-secret
        Distribution:
          NodeId: {{identity}}
          Role: {{(identity == "local" ? "Node" : "Controller")}}
          StateDirectory: {{identity}}-state
          Nodes:
            edge:
              Endpoint: https://{{identity}}
        WafInfos:
          Listens:
            main:
              Port: 8080
          Certs:
            PemFile: {{identity}}.pem
        FileServer:
          Items:
            upload:
              Root: {{identity}}-uploads
        Protect:
          CheckArgsFile: {{identity}}-rules
        SpeedLimit:
          Value: {{value}}
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalIdentitySecretsPathsAndAbsentSectionsWin(bool ly)
    {
        var path = ly ? "config.ly" : "config.yaml";
        var output = NodeLocalConfigurationOverlay.Compose(path, Content(ly, "local", "old"), Content(ly, "controller", "new"));
        using var result = NodeLocalConfigurationOverlay.ReadConfiguration(path, output);
        Assert.Equal("local", result["NodeCommunication:NodeId"]);
        Assert.Equal("local-secret", result["NodeCommunication:Peers:peer:Keys:key"]);
        Assert.Equal("Node", result["Distribution:Role"]);
        Assert.Equal("https://local", result["Distribution:Nodes:edge:Endpoint"]);
        Assert.Equal("local-uploads", result["FileServer:Items:upload:Root"]);
        Assert.Equal("local-rules", result["Protect:CheckArgsFile"]);
        Assert.Equal("new", result["SpeedLimit:Value"]);
        var absent = NodeLocalConfigurationOverlay.Compose(path, ly ? "SpeedLimit { Value = old }" : "SpeedLimit:\n  Value: old\n", Content(ly, "controller", "new"));
        using var noIdentity = NodeLocalConfigurationOverlay.ReadConfiguration(path, absent);
        Assert.Null(noIdentity["NodeCommunication:NodeId"]);
        Assert.Null(noIdentity["Distribution:Role"]);
        Assert.Null(noIdentity["Protect:CheckArgsFile"]);
    }

    [Fact]
    public void CaseInsensitiveIdentityAndUnknownSectionsStayLocal()
    {
        var result = NodeLocalConfigurationOverlay.Compose("config.yaml", "nodecommunication:\n  NodeId: local\nCustomPlugin:\n  Secret: local\n",
            "NODECOMMUNICATION:\n  NodeId: remote\nCustomPlugin:\n  Secret: remote\nNewPlugin:\n  Path: injected\n");
        using var config = NodeLocalConfigurationOverlay.ReadConfiguration("config.yaml", result);
        Assert.Equal("local", config["NodeCommunication:NodeId"]);
        Assert.Equal("local", config["CustomPlugin:Secret"]);
        Assert.Null(config["NewPlugin:Path"]);
    }

    [Theory]
    [InlineData("NodeCommunication:NodeId: remote")]
    [InlineData("NodeCommunication:\n  NodeId: a\nnodecommunication:\n  NodeId: b")]
    public void AmbiguousYamlKeysAreRejected(string remote) => Assert.Throws<InvalidOperationException>(() =>
        NodeLocalConfigurationOverlay.Compose("config.yaml", "SpeedLimit: {}", remote));

    [Fact]
    public void LyVariablesAreMaterializedAndUnresolvedVariableLiteralsFailClosed()
    {
        var local = "var identity = local\nNodeCommunication { NodeId = $identity }\nSpeedLimit { Value = old }";
        var remote = "var identity = remote\nNodeCommunication { NodeId = $identity }\nSpeedLimit { Value = new }";
        var result = NodeLocalConfigurationOverlay.Compose("config.ly", local, remote);
        using var config = NodeLocalConfigurationOverlay.ReadConfiguration("config.ly", result);
        Assert.Equal("local", config["NodeCommunication:NodeId"]);
        Assert.Equal("new", config["SpeedLimit:Value"]);
        Assert.DoesNotContain("$identity", result);
        Assert.Throws<InvalidOperationException>(() => NodeLocalConfigurationOverlay.Compose("config.ly", local,
            "SpeedLimit { Value = \"${UNRESOLVED_OVERLAY_8d3727}\" }"));
    }

    [Fact]
    public void LySiteCanChangeUpstreamButCannotChangeListenerOrImport()
    {
        const string local = "NodeCommunication { NodeId = local }\n:8080 { reverse_proxy localhost:9000 }";
        var result = NodeLocalConfigurationOverlay.Compose("config.ly", local, ":8080 { reverse_proxy localhost:9001 }");
        using var config = NodeLocalConfigurationOverlay.ReadConfiguration("config.ly", result);
        Assert.Equal("local", config["NodeCommunication:NodeId"]);
        Assert.Contains(config.AsEnumerable(), pair => pair.Value == "http://localhost:9001");
        Assert.Throws<InvalidOperationException>(() => NodeLocalConfigurationOverlay.Compose("config.ly", local, ":9090 { reverse_proxy localhost:9001 }"));
        Assert.Throws<InvalidOperationException>(() => NodeLocalConfigurationOverlay.Compose("config.ly", local, "import secret.ly"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareRestartCommitAndRollbackUseNodeSpecificCandidate(bool ly)
    {
        var oldPath = SharedData.ConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "node-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ly ? "config.ly" : "config.yaml");
        SharedData.ConfigFilePath = path;
        IConfigurationRoot? root = null;
        try
        {
            await File.WriteAllTextAsync(path, Content(ly, "local", "old"));
            root = ly ? new ConfigurationBuilder().AddLyConfig(path, false).Build()
                : new ConfigurationBuilder().AddYamlFile(path, false, false).Build();
            var versions = new ConfigVersionService(new HttpContextAccessor());
            var options = new StaticOptionsMonitor<DistributionOptions>(new()
            { NodeId = "local", Role = "Node", StateDirectory = Path.Combine(directory, "state") });
            DistributionNodeService NewService() => new(options, versions, new ConfigurationLifecycleService(), root);
            var remote = Content(ly, "controller", "new");
            var request = new PrepareConfigurationRequest("overlay", "source", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote))), remote);
            var service = NewService();
            var prepared = await service.PrepareAsync(request, default);
            Assert.True(prepared.Ack, prepared.Error);
            Assert.Equal("old", root["SpeedLimit:Value"]);
            var statePath = Path.Combine(directory, "state", "node-state.json");
            var preparedState = await File.ReadAllTextAsync(statePath);
            service = NewService();
            var committed = await service.CommitAsync("overlay", default);
            Assert.True(committed.Ack, committed.Error);
            Assert.Equal("new", root["SpeedLimit:Value"]);
            Assert.Equal("local-secret", root["NodeCommunication:Peers:peer:Keys:key"]);
            await File.WriteAllTextAsync(statePath, preparedState);
            service = NewService();
            Assert.NotNull(await service.ReconcileCommandAsync("configuration.commit", "overlay", null, default));
            Assert.NotNull(await service.ReconcileCommandAsync("configuration.prepare", "overlay", request, default));
            // Local key rotation after deployment must survive compensation.
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("local-secret", "rotated-secret"));
            var rolledBack = await service.RollbackAsync("overlay", default);
            Assert.True(rolledBack.Ack, rolledBack.Error);
            Assert.Equal("old", root["SpeedLimit:Value"]);
            Assert.Equal("rotated-secret", root["NodeCommunication:Peers:peer:Keys:key"]);
            var next = request with { DeploymentId = "next" };
            Assert.True((await service.PrepareAsync(next, default)).Ack);
            var baseline = await File.ReadAllBytesAsync(path);
            await File.AppendAllTextAsync(path, "\n# local edit\n");
            Assert.False((await service.CommitAsync("next", default)).Ack);
            await File.WriteAllBytesAsync(path, baseline);
            await File.AppendAllTextAsync(Path.Combine(directory, "state", "prepared", "next.json.overlay"), "\n# tampered\n");
            Assert.False((await service.CommitAsync("next", default)).Ack);
            Assert.Equal(baseline, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            SharedData.ConfigFilePath = oldPath;
            Directory.Delete(directory, true);
        }
    }
}
