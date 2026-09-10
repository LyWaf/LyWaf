namespace LyWaf.Services.Sites;

/// <summary>解析站点 Host 约束与监听端口、证书名称的关联，不发起网络请求。</summary>
public static class SiteBindingResolver
{
    public static bool UsesPort(IReadOnlyList<string>? hosts, int port) =>
        hosts is not { Count: > 0 } || hosts.Any(h => Split(h).Port is not { } expected || expected == port);

    public static bool UsesCertificate(IReadOnlyList<string>? hosts, string certificateDomain) =>
        hosts?.Any(h => MatchesCertificate(Split(h).Host, certificateDomain.TrimEnd('.'))) == true;

    private static bool MatchesCertificate(string host, string certificate)
    {
        if (host.Equals(certificate, StringComparison.OrdinalIgnoreCase)) return true;
        if (!certificate.StartsWith("*.", StringComparison.Ordinal) || host.Contains('*')) return false;
        var suffix = certificate[1..];
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length &&
            !host[..^suffix.Length].Contains('.');
    }

    private static (string Host, int? Port) Split(string value)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end >= 0)
                return (value[1..end], end + 1 < value.Length && value[end + 1] == ':' && int.TryParse(value[(end + 2)..], out var ipv6Port) ? ipv6Port : null);
        }
        var colon = value.LastIndexOf(':');
        return colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out var port)
            ? (value[..colon].TrimEnd('.'), port) : (value.TrimEnd('.'), null);
    }
}
