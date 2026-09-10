using System.Text;
using System.Text.Json;
using LyWaf.Config;

namespace LyWaf.Services.Backup;

/// <summary>仅在内存中构造备份候选配置，合并暂存补丁进行语义检查，不改变全局路径。</summary>
internal static class BackupConfigurationCandidate
{
    public static IConfigurationRoot Build(string configPath, string content, string? patchPath)
    {
        var builder = new ConfigurationBuilder();
        if (configPath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase))
        {
            var variables = Environment.GetEnvironmentVariables().Keys.Cast<object>()
                .ToDictionary(k => k.ToString()!, k => Environment.GetEnvironmentVariable(k.ToString()!) ?? "");
            var data = LyToAppSettingsConverter.TransformToAppSettings(LyConfigParser.Parse(content, variables));
            builder.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(data)));
        }
        else
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));
            builder.AddInMemoryCollection(YamlConfigurationFileParser.Parse(input));
        }
        if (patchPath != null) builder.Add(new LbPatchSource(patchPath, validateSource: false));
        return builder.Build();
    }
}
