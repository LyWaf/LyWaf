using System.Text.Json;

namespace LyWaf.Services.Discovery;

/// <summary>从本地 JSON 文件读取上游实例，适合外部脚本或配置管理工具原子替换文件。</summary>
public sealed class FileDiscoveryProvider : IServiceDiscoveryProvider
{
    public string Type => "File";

    public async Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(
        string providerId, DiscoveryProviderOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Path)) throw new InvalidOperationException($"发现 Provider {providerId} 未配置 Path");
        var path = Path.GetFullPath(options.Path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > Math.Max(1, options.MaxPayloadBytes))
            throw new InvalidDataException($"发现文件超过 {options.MaxPayloadBytes} 字节限制");
        var document = await JsonSerializer.DeserializeAsync<FileDocument>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
            ?? throw new InvalidDataException($"发现文件为空: {path}");
        return document.Destinations.Select(x => new DiscoveredDestination(
            ValidateId(x.Id), ValidateAddress(x.Address), x.Metadata)).ToArray();
    }

    private static string ValidateId(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException("发现实例 Id 不能为空") : value.Trim();

    internal static string ValidateAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException($"发现实例 Address 必须是 HTTP/HTTPS 绝对 URI: {value}");
        return value.EndsWith('/') ? value : value + "/";
    }

    private sealed class FileDocument
    {
        public List<FileDestination> Destinations { get; set; } = [];
    }

    private sealed class FileDestination
    {
        public string Id { get; set; } = "";
        public string Address { get; set; } = "";
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
