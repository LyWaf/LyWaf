using System.Net.Sockets;
using System.Text.Json;

namespace LyWaf.Services.Discovery;

/// <summary>查询 Docker Engine 容器列表，按 label 选择运行中容器并发布容器网络地址。</summary>
public sealed class DockerDiscoveryProvider : IServiceDiscoveryProvider
{
    public string Type => "Docker";

    public async Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(
        string providerId, DiscoveryProviderOptions options, CancellationToken cancellationToken)
    {
        using var client = CreateClient(options.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(options.Endpoint, options.Selector));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using var document = await BoundedJsonResponse.ReadAsync(response, options.MaxPayloadBytes, cancellationToken);
        return ParseContainers(document.RootElement, providerId, options);
    }

    internal static IReadOnlyList<DiscoveredDestination> ParseContainers(
        JsonElement root, string providerId, DiscoveryProviderOptions options)
    {
        var result = new List<DiscoveredDestination>();
        foreach (var container in root.EnumerateArray())
        {
            if (!container.TryGetProperty("Id", out var idNode)) continue;
            var containerId = idNode.GetString() ?? "";
            if (containerId.Length == 0) continue;
            var labels = ReadStringMap(container, "Labels");
            if (!MatchesSelector(labels, options.Selector)) continue;
            if (!container.TryGetProperty("NetworkSettings", out var networkSettings) ||
                !networkSettings.TryGetProperty("Networks", out var networks)) continue;
            var index = 0;
            foreach (var network in networks.EnumerateObject())
            {
                var address = network.Value.TryGetProperty("IPAddress", out var ipNode) ? ipNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(address)) continue;
                var port = options.Port;
                if (port is < 1 or > 65535) throw new InvalidOperationException($"Docker Provider {providerId} 必须配置容器 Port");
                result.Add(new DiscoveredDestination(
                    $"docker-{containerId[..Math.Min(12, containerId.Length)]}-{index++}",
                    FileDiscoveryProvider.ValidateAddress($"{options.Scheme}://{address}:{port}/"),
                    new Dictionary<string, string>
                    {
                        ["docker.container"] = containerId,
                        ["docker.network"] = network.Name
                    }));
            }
        }
        return result;
    }

    private static HttpClient CreateClient(string endpoint)
    {
        if (endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            var socketPath = endpoint["unix://".Length..];
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        }
        var baseAddress = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:2375" : endpoint;
        return new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(10) };
    }

    private static Uri BuildRequestUri(string endpoint, string selector)
    {
        var path = "/v1.41/containers/json?all=false";
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var filters = JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [selector] });
            path += "&filters=" + Uri.EscapeDataString(filters);
        }
        return endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            ? new Uri("http://docker" + path)
            : new Uri(path, UriKind.Relative);
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object) return result;
        foreach (var item in map.EnumerateObject()) result[item.Name] = item.Value.GetString() ?? "";
        return result;
    }

    private static bool MatchesSelector(IReadOnlyDictionary<string, string> labels, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return true;
        var separator = selector.IndexOf('=');
        return separator < 0
            ? labels.ContainsKey(selector.Trim())
            : labels.TryGetValue(selector[..separator].Trim(), out var value) &&
              value.Equals(selector[(separator + 1)..].Trim(), StringComparison.Ordinal);
    }
}
