
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using LyWaf.Services.Param;
using LyWaf.Shared;
using Microsoft.Extensions.Options;

namespace LyWaf.Utils
{

    public static class RequestUtil
    {
        private static readonly ConditionalWeakTable<GlobalOptions, ProxyTrust> ProxyPolicies = new();
        private const int MaxForwardedCharacters = 16384;
        private const int MaxForwardedHops = 64;

        public static string GetClientIp(HttpRequest request)
        {
            if (SharedData.IsFirstServer) return RemoteAddress(request);
            try
            {
                var options = request.HttpContext.RequestServices?.GetService<IOptionsMonitor<GlobalOptions>>()?.CurrentValue;
                return options is null ? RemoteAddress(request) : GetClientIp(request, options);
            }
            catch (OptionsValidationException) { return RemoteAddress(request); }
        }

        internal static string GetClientIp(HttpRequest request, GlobalOptions options)
        {
            var remote = request.HttpContext.Connection.RemoteIpAddress;
            if (remote is null) return "";
            remote = Normalize(remote);
            if (options.IsFirstServer) return remote.ToString();
            ProxyTrust trust;
            try { trust = ProxyPolicies.GetValue(options, static value => new ProxyTrust(value)); }
            catch (FormatException) { return remote.ToString(); }
            if (!trust.Contains(remote)) return remote.ToString();

            var header = request.Headers.ContainsKey("X-Forwarded-For") ? "X-Forwarded-For"
                : request.Headers.ContainsKey("X-Real-IP") ? "X-Real-IP"
                : request.Headers.ContainsKey("Forwarded") ? "Forwarded" : null;
            if (header is null) return remote.ToString();
            var values = request.Headers[header];
            if (values.Count is 0 or > MaxForwardedHops || values.Sum(x => (long)(x?.Length ?? 0)) > MaxForwardedCharacters)
                return remote.ToString();

            // Split only HTTP list separators; IPv6 and endpoint parsing is delegated to .NET.
            string[] hops;
            if (header == "Forwarded")
            {
                hops = request.Headers.GetCommaSeparatedValues(header);
                // Strict subset: reject empty list elements and quoted commas rather than guessing boundaries.
                if (hops.Length != values.Sum(x => (x?.Count(c => c == ',') ?? 0) + 1)) return remote.ToString();
            }
            else hops = values.SelectMany(x => (x ?? "").Split(',')).ToArray();
            if (hops.Length is 0 or > MaxForwardedHops || (header == "X-Real-IP" && hops.Length != 1))
                return remote.ToString();

            var addresses = new List<IPAddress>(hops.Length);
            foreach (var hop in hops)
            {
                var value = header == "Forwarded" ? ReadForwardedFor(hop) : hop.Trim();
                if (!TryAddress(value, out var address)) return remote.ToString();
                addresses.Add(address!);
            }
            var current = remote;
            for (var i = addresses.Count - 1; i >= 0 && trust.Contains(current); i--)
                current = addresses[i];
            return current.ToString();
        }

        private static string? ReadForwardedFor(string entry)
        {
            if (!NameValueWithParametersHeaderValue.TryParse(entry, out var parsed)) return null;
            var parameters = new[] { new NameValueHeaderValue(parsed.Name, parsed.Value) }.Concat(parsed.Parameters).ToArray();
            if (parameters.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Length)
                return null;
            var value = parameters.SingleOrDefault(x => x.Name.Equals("for", StringComparison.OrdinalIgnoreCase))?.Value;
            if (value is not null && value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            return value;
        }

        private static bool TryAddress(string? value, out IPAddress? address)
        {
            address = null;
            // Scoped addresses, obfuscated identifiers and host names are not client identities.
            if (string.IsNullOrWhiteSpace(value) || value.Contains('%')) return false;
            if (!IPAddress.TryParse(value, out address))
            {
                if (!IPEndPoint.TryParse(value, out var endpoint)) return false;
                address = endpoint.Address;
            }
            address = Normalize(address);
            return true;
        }

        private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        private static string RemoteAddress(HttpRequest request) => request.HttpContext.Connection.RemoteIpAddress is { } ip ? Normalize(ip).ToString() : "";

        public static bool IsValidProxyConfiguration(GlobalOptions options)
        {
            try { _ = ProxyPolicies.GetValue(options, static value => new ProxyTrust(value)); return true; }
            catch (FormatException) { return false; }
        }

        private sealed class ProxyTrust
        {
            private readonly HashSet<IPAddress> _proxies = [];
            private readonly List<System.Net.IPNetwork> _networks = [];

            public ProxyTrust(GlobalOptions options)
            {
                foreach (var value in options.TrustedProxies ?? [])
                {
                    if (value is null || value.Contains('%') || !IPAddress.TryParse(value, out var address))
                        throw new FormatException("Invalid trusted proxy IP.");
                    _proxies.Add(Normalize(address));
                }
                foreach (var value in options.TrustedNetworks ?? [])
                {
                    if (value is null || value.Contains('%') || !System.Net.IPNetwork.TryParse(value, out var network))
                        throw new FormatException("Invalid trusted proxy CIDR.");
                    if (network.BaseAddress.IsIPv4MappedToIPv6)
                    {
                        if (network.PrefixLength < 96) throw new FormatException("Mapped IPv4 CIDR must have a prefix >= 96.");
                        network = new System.Net.IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
                    }
                    _networks.Add(network);
                }
            }
            public bool Contains(IPAddress address) => _proxies.Contains(address) || _networks.Any(x => x.Contains(address));
        }

        public static string RecordRuquest(HttpRequest request)
        {
            return RecordRuquest("win-curl", request, null);
        }

        public static string RecordRuquest(HttpRequest request, string? body = null)
        {
            return RecordRuquest("win-curl", request, body);
        }

        public static string RecordRuquest(string methd, HttpRequest request, string? body = null)
        {
            StringBuilder sb = new();
            sb.Append("curl ");
            sb.Append("'" + GetRequestUrl(request) + "' ");
            if (request.Method != "GET")
            {
                sb.Append("\\\n-X " + request.Method + " ");
            }
            foreach (var header in request.Headers)
            {
                sb.Append(string.Format("\\\n-H '{0}:{1}' ", header.Key, header.Value));
            }
            if (body != null && body.Length > 0)
            {
                sb.Append("\\\n--data-raw '" + body + "'");
            }
            return sb.ToString();
        }

        public static string GetRequestUrl(HttpRequest request)
        {
            return string.Format("{0}://{1}{2}", request.Scheme, request.Host, request.Path);
        }

        public static string GetRequestBaseUrl(HttpRequest request)
        {
            return string.Format("{0}://{1}", request.Scheme, request.Host);
        }

    }
}
