using System.ComponentModel.DataAnnotations;
using LyWaf.Plugins.Core;

namespace LyWaf.Plugins.Sys.HttpUpload;

/// <summary>Upload endpoint settings with one trusted destination directory.</summary>
public sealed class HttpUploadOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "/_upload";
    public List<string> Hosts { get; set; } = ["*"];
    public bool RequireHttps { get; set; } = true;
    [PluginConfiguration(Sensitive = true, DescriptionKey = "plugins.http-upload.Key.description")]
    public string Key { get; set; } = "";
    [Range(1, 3600)]
    public int SignatureWindowSeconds { get; set; } = 300;
    [PluginConfiguration(RequiresRestart = true)]
    public string NonceDirectory { get; set; } = "plugin_data/http-upload/nonces";
    [PluginConfiguration(LabelKey = "plugins.http-upload.Directory.label", DescriptionKey = "plugins.http-upload.Directory.description")]
    public string Directory { get; set; } = "";
    public bool AllowZip { get; set; }
    [Range(typeof(long), "1", "1099511627776")]
    public long MaxUploadBytes { get; set; } = 104857600;
    [Range(typeof(long), "1", "1099511627776")]
    public long MaxExtractedBytes { get; set; } = 524288000;
    [Range(1, 10000)]
    public int MaxEntries { get; set; } = 1000;
}
