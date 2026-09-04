using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace LyWaf.Services.Discovery;

/// <summary>读取 Kubernetes EndpointSlice，把 Ingress、Gateway API 或 CRD 背后 Service 的就绪端点发布给 YARP。</summary>
public sealed class KubernetesDiscoveryProvider : IServiceDiscoveryProvider
{
    public string Type => "Kubernetes";

    public async Task<IReadOnlyList<DiscoveredDestination>> DiscoverAsync(
        string providerId, DiscoveryProviderOptions options, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "https://kubernetes.default.svc" : options.Endpoint.TrimEnd('/');
        using var client = CreateClient(options);
        var tokenPath = string.IsNullOrWhiteSpace(options.TokenFile)
            ? "/var/run/secrets/kubernetes.io/serviceaccount/token"
            : options.TokenFile;
        string? token = null;
        if (File.Exists(tokenPath))
            token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim();

        var services = await ResolveServicesAsync(client, endpoint, token, providerId, options, cancellationToken);
        var result = new List<DiscoveredDestination>();
        foreach (var service in services)
        {
            var selector = $"kubernetes.io/service-name={service}";
            if (!string.IsNullOrWhiteSpace(options.Selector)) selector += "," + options.Selector;
            var uri = $"{endpoint}/apis/discovery.k8s.io/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/endpointslices?labelSelector={Uri.EscapeDataString(selector)}";
            using var document = await GetJsonAsync(client, uri, token, options.MaxPayloadBytes, cancellationToken);
            result.AddRange(ParseEndpointSlices(document.RootElement, options, service));
        }
        return result;
    }

    internal static IReadOnlyList<DiscoveredDestination> ParseEndpointSlices(
        System.Text.Json.JsonElement root, DiscoveryProviderOptions options, string? serviceOverride = null)
    {
        var result = new List<DiscoveredDestination>();
        if (!root.TryGetProperty("items", out var items)) return result;
        foreach (var slice in items.EnumerateArray())
        {
            var sliceName = slice.GetProperty("metadata").GetProperty("name").GetString() ?? "slice";
            var port = ResolvePort(slice, options.Port);
            if (port is < 1 or > 65535) continue;
            if (!slice.TryGetProperty("endpoints", out var endpoints)) continue;
            var index = 0;
            foreach (var endpointNode in endpoints.EnumerateArray())
            {
                if (endpointNode.TryGetProperty("conditions", out var conditions) &&
                    conditions.TryGetProperty("ready", out var ready) && ready.ValueKind == System.Text.Json.JsonValueKind.False) continue;
                if (!endpointNode.TryGetProperty("addresses", out var addresses)) continue;
                foreach (var addressNode in addresses.EnumerateArray())
                {
                    var address = addressNode.GetString();
                    if (string.IsNullOrWhiteSpace(address)) continue;
                    var uriHost = address.Contains(':') ? $"[{address}]" : address;
                    result.Add(new DiscoveredDestination($"k8s-{SanitizeId(sliceName)}-{index++}",
                        FileDiscoveryProvider.ValidateAddress($"{options.Scheme}://{uriHost}:{port}/"),
                        new Dictionary<string, string>
                        {
                            ["kubernetes.namespace"] = options.Namespace,
                            ["kubernetes.service"] = serviceOverride ?? options.Service,
                            ["kubernetes.endpointslice"] = sliceName
                        }));
                }
            }
        }
        return result;
    }

    internal static IReadOnlyList<string> ResolveServiceNames(
        System.Text.Json.JsonElement root, string resourceKind, string fallbackService)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resourceKind.Equals("Service", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(fallbackService)) names.Add(fallbackService);
        }
        else if (resourceKind.Equals("Ingress", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("spec", out var spec))
            {
                AddIngressBackend(spec, "defaultBackend", names);
                if (spec.TryGetProperty("rules", out var rules))
                foreach (var rule in rules.EnumerateArray())
                    if (rule.TryGetProperty("http", out var http) && http.TryGetProperty("paths", out var paths))
                    foreach (var path in paths.EnumerateArray()) AddIngressBackend(path, "backend", names);
            }
        }
        else if (resourceKind.Equals("GatewayApi", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("spec", out var spec) && spec.TryGetProperty("rules", out var rules))
            foreach (var rule in rules.EnumerateArray())
                if (rule.TryGetProperty("backendRefs", out var refs))
                foreach (var backend in refs.EnumerateArray())
                {
                    var kind = backend.TryGetProperty("kind", out var kindNode) ? kindNode.GetString() : "Service";
                    if (!string.Equals(kind, "Service", StringComparison.OrdinalIgnoreCase)) continue;
                    if (backend.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString())) names.Add(name.GetString()!);
                }
        }
        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<string>> ResolveServicesAsync(
        HttpClient client, string endpoint, string? token, string providerId,
        DiscoveryProviderOptions options, CancellationToken cancellationToken)
    {
        if (options.ResourceKind.Equals("Service", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Service)) throw new InvalidOperationException($"Kubernetes Provider {providerId} 未配置 Service");
            return [options.Service];
        }
        if (string.IsNullOrWhiteSpace(options.ResourceName))
            throw new InvalidOperationException($"Kubernetes Provider {providerId} 未配置 ResourceName");
        var resourcePath = options.ResourceKind.Equals("Ingress", StringComparison.OrdinalIgnoreCase)
            ? $"apis/networking.k8s.io/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/ingresses/{Uri.EscapeDataString(options.ResourceName)}"
            : options.ResourceKind.Equals("GatewayApi", StringComparison.OrdinalIgnoreCase)
                ? $"apis/gateway.networking.k8s.io/v1/namespaces/{Uri.EscapeDataString(options.Namespace)}/httproutes/{Uri.EscapeDataString(options.ResourceName)}"
                : throw new InvalidOperationException($"Kubernetes ResourceKind 不受支持: {options.ResourceKind}");
        using var document = await GetJsonAsync(client, $"{endpoint}/{resourcePath}", token, options.MaxPayloadBytes, cancellationToken);
        var services = ResolveServiceNames(document.RootElement, options.ResourceKind, options.Service);
        if (services.Count == 0) throw new InvalidDataException($"{options.ResourceKind} 未引用任何 Service");
        return services;
    }

    private static async Task<System.Text.Json.JsonDocument> GetJsonAsync(
        HttpClient client, string uri, string? token, long maxBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await BoundedJsonResponse.ReadAsync(response, maxBytes, cancellationToken);
    }

    private static void AddIngressBackend(System.Text.Json.JsonElement parent, string property, ISet<string> names)
    {
        if (!parent.TryGetProperty(property, out var backend) || !backend.TryGetProperty("service", out var service) ||
            !service.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString())) return;
        names.Add(name.GetString()!);
    }

    private static HttpClient CreateClient(DiscoveryProviderOptions options)
    {
        var caPath = string.IsNullOrWhiteSpace(options.CaFile)
            ? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt"
            : options.CaFile;
        if (!File.Exists(caPath)) return new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var authority = X509Certificate2.CreateFromPemFile(caPath);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate == null) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate)) &&
                       (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static int ResolvePort(System.Text.Json.JsonElement slice, int configured)
    {
        if (configured is >= 1 and <= 65535) return configured;
        if (!slice.TryGetProperty("ports", out var ports)) return 0;
        foreach (var portNode in ports.EnumerateArray())
            if (portNode.TryGetProperty("port", out var value) && value.TryGetInt32(out var port)) return port;
        return 0;
    }

    private static string SanitizeId(string value) =>
        new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
