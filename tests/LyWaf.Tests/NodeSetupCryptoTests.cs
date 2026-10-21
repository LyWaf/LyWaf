using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.AntiBot;
using LyWaf.Services.Captcha;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class NodeSetupCryptoTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-setup-crypto-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ControllerKeysAreStableAndOnlyPublicAuthorityMaterialIsDistributed()
    {
        var store = new NodeSetupStore(root);
        await store.ConfigureAsync("Controller", null, null, default);
        var before = store.Status();
        var one = await store.CreateCredentialAsync("one", default);
        var two = await store.CreateCredentialAsync("two", default);
        var a = NodeSetupStore.DecodeCredential(one.Credential);
        var b = NodeSetupStore.DecodeCredential(two.Credential);
        var restarted = new NodeSetupStore(root);
        var controller = restarted.Active!.Controller!;
        Assert.True(NodeSetupCrypto.ValidPrivateKey(controller.ReleasePrivateKeyPem));
        Assert.Equal(NodeSetupCrypto.PublicKey(controller.ReleasePrivateKeyPem), a.ReleasePublicKeyPem);
        Assert.Equal(a.ReleasePublicKeyPem, b.ReleasePublicKeyPem);
        Assert.Equal(a.ChallengeSecret, b.ChallengeSecret);
        Assert.NotEqual(a.Secret, b.Secret);
        Assert.Equal(controller.ClusterId, a.ReleaseTrustDomain);
        Assert.Equal(before.NodeId, controller.NodeId);
        Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(a));
        using var publicOnly = ECDsa.Create();
        publicOnly.ImportFromPem(a.ReleasePublicKeyPem);
        Assert.ThrowsAny<CryptographicException>(() => publicOnly.ExportPkcs8PrivateKeyPem());
        var visible = JsonSerializer.Serialize(new { settings = restarted.Status(), peers = restarted.Peers() });
        Assert.DoesNotContain(controller.ReleasePrivateKeyPem, visible);
        Assert.DoesNotContain(controller.ChallengeSecret, visible);
    }

    [Fact]
    public async Task ProjectedKeysSignAtControllerAndVerifyAtNodeWithoutGivingNodeSigningAuthority()
    {
        var controlRoot = Path.Combine(root, "controller");
        var store = new NodeSetupStore(controlRoot);
        await store.ConfigureAsync("Controller", null, null, default);
        var issued = await store.CreateCredentialAsync("node", default);
        var nodeRoot = Path.Combine(root, "node");
        await new NodeSetupStore(nodeRoot).ConfigureAsync("Node", "https://controller.example:7030/", issued.Credential, default);
        using var control = Services(controlRoot);
        using var node = Services(nodeRoot);
        var options = control.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        var receiving = node.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        var generation = new RuntimeStateMutation(CcPolicyGeneration.Domain, "policy", options.NodeId, 1, DateTimeOffset.UtcNow,
            null, false, JsonSerializer.SerializeToElement(new CcPolicyGeneration(1, Guid.NewGuid().ToString("N"))));
        var signed = CcReleaseAuthorization.Sign(generation, options);
        Assert.NotNull(signed.CcReleaseProof);
        CcReleaseAuthorization.Verify(signed, receiving);
        Assert.Empty(receiving.CcReleasePrivateKeyPem);
        Assert.Equal(options.NodeId, receiving.CcCaptchaAuthorityNodeId);
        Assert.Equal(options.CcReleaseTrustDomain, receiving.CcReleaseTrustDomain);
        Assert.Null(CcReleaseAuthorization.Sign(generation, receiving).CcReleaseProof);
        Assert.Equal(control.GetRequiredService<IOptionsMonitor<AntiBotOptions>>().CurrentValue.Secret,
            node.GetRequiredService<IOptionsMonitor<AntiBotOptions>>().CurrentValue.Secret);
        Assert.Equal("Authority", node.GetRequiredService<IOptions<CaptchaStoreOptions>>().Value.Provider);
        Assert.DoesNotContain(options.CcReleasePrivateKeyPem, JsonSerializer.Serialize(NodeSetupStore.DecodeCredential(issued.Credential)));
    }

    [Fact]
    public void LegacyControllerStateIsMigratedOnceWithoutChangingIdentityOrDroppingPeers()
    {
        Directory.CreateDirectory(root);
        var old = new NodeSetupState(1, 7, "Controller", new("legacy-cluster", "controller-known",
            [new("node-known", "known", "key", new string('a', 64))]));
        File.WriteAllText(Path.Combine(root, "setup.json"), JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var migrated = new NodeSetupStore(root);
        var key = migrated.Active!.Controller!.ReleasePrivateKeyPem;
        Assert.Equal(8, migrated.Status().Revision);
        Assert.Equal("controller-known", migrated.Status().NodeId);
        Assert.Equal("node-known", Assert.Single(migrated.Peers()).NodeId);
        Assert.False(migrated.Status().RestartRequired);
        var restarted = new NodeSetupStore(root);
        Assert.Equal(8, restarted.Status().Revision);
        Assert.Equal(key, restarted.Active!.Controller!.ReleasePrivateKeyPem);
    }

    [Fact]
    public void MigrationPreservesExistingValidAuthorityAndChallengeConfiguration()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "setup.json"), JsonSerializer.Serialize(new NodeSetupState(1, 3, "Controller",
            new("legacy-cluster", "controller", [])), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var privateKey = NodeSetupCrypto.NewPrivateKey();
        const string secret = "existing-shared-challenge-secret-more-than-32-bytes";
        var migrated = new NodeSetupStore(root, privateKey, secret, "legacy:trust-domain");
        Assert.Equal(privateKey, migrated.Active!.Controller!.ReleasePrivateKeyPem);
        Assert.Equal(secret, migrated.ChallengeSecret);
        Assert.Equal("legacy:trust-domain", migrated.Active.Controller.ReleaseTrustDomain);
    }

    [Fact]
    public void CorruptAuthorityIsNotSilentlyReplaced()
    {
        Directory.CreateDirectory(root);
        var json = JsonSerializer.Serialize(new NodeSetupState(1, 7, "Controller", new("cluster", "controller", [],
            "not-a-private-key", new string('a', 64), "cluster")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(Path.Combine(root, "setup.json"), json);
        Assert.Throws<InvalidDataException>(() => new NodeSetupStore(root));
        Assert.Equal(json, File.ReadAllText(Path.Combine(root, "setup.json")));
    }

    [Fact]
    public async Task CredentialCannotSmuggleAPrivateKeyAsAPublicAuthority()
    {
        var store = new NodeSetupStore(root);
        await store.ConfigureAsync("Controller", null, null, default);
        var issued = await store.CreateCredentialAsync("node", default);
        var original = NodeSetupStore.DecodeCredential(issued.Credential);
        var secret = new NodeSetupStore(root).Active!.Controller!.ReleasePrivateKeyPem;
        var invalid = original with { ReleasePublicKeyPem = secret };
        var encoded = "lywaf_node_" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invalid,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<ArgumentException>(() => NodeSetupStore.DecodeCredential(encoded));
    }

    private static ServiceProvider Services(string directory)
    {
        var configuration = new ConfigurationManager();
        configuration["AntiBot:Enabled"] = "true";
        configuration["AntiBot:Mode"] = "Challenge";
        var services = new ServiceCollection();
        configuration.AddNodeSetup(services, directory);
        services.AddOptions<RuntimeStateOptions>().Bind(configuration.GetSection("RuntimeState"));
        services.AddOptions<AntiBotOptions>().Bind(configuration.GetSection("AntiBot"));
        services.AddLyWafCaptcha(configuration);
        Assert.DoesNotContain(configuration.AsEnumerable(), pair => pair.Value?.Contains("PRIVATE KEY", StringComparison.Ordinal) == true);
        Assert.Null(configuration["AntiBot:Secret"]);
        return services.BuildServiceProvider();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
