using System.Runtime.CompilerServices;
using LyWaf.Config;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateDocumentationAuthorizationTests
{
    [Fact]
    public void Document36ExampleBindsThroughProductionRegistrationAndAuthorizesBothDirections()
    {
        var doc = File.ReadAllText(Path.Combine(Repository(), "docs", "36-分布式运行状态.md"));
        var start = doc.IndexOf("```ly", StringComparison.Ordinal) + "```ly".Length;
        Assert.True(start >= "```ly".Length);
        var end = doc.IndexOf("```", start, StringComparison.Ordinal);
        var sample = doc[start..end].Replace("${RUNTIME_STATE_SECRET}", new string('k', 32));
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-doc36-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "sample.ly");
            File.WriteAllText(path, sample);
            using var configuration = (IDisposable)new ConfigurationBuilder().AddLyConfig(path, false).Build();
            using var services = new ServiceCollection().AddLyWafRuntimeState((IConfiguration)configuration).BuildServiceProvider();
            var options = services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            Assert.True(RuntimeStateReplicationAuthorization.IsValidConfiguration(options));
            Assert.Equal("edge-01", options.NodeId);
            Assert.Equal(1, options.Peers["edge-02"].ProtocolVersion);
            var now = DateTimeOffset.UtcNow;
            var local = new RuntimeStateMutation("ip.blocked", "192.0.2.1", "edge-01", 1, now, now.AddMinutes(5), false, null);
            Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeSend(options, "edge-02", local));
            Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "edge-02", [local with { NodeId = "edge-02" }]));
            Assert.NotNull(RuntimeStateReplicationAuthorization.AuthorizeSend(options, "edge-02", local with { Domain = "captcha.challenge" }));
            Assert.NotNull(RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "edge-02", [local with { NodeId = "edge-03" }]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Repository([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
