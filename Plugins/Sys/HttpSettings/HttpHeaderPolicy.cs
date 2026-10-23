namespace LyWaf.Plugins.Sys.HttpSettings;

/// <summary>Shared protocol guards, independent of the proxy, WAF and plugin lifecycle.</summary>
public static class HttpHeaderPolicy
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Encoding", "Content-Range", "Transfer-Encoding", "Connection", "Keep-Alive",
        "Proxy-Connection", "TE", "Trailer", "Upgrade", "HTTP2-Settings", "Forwarded", "X-Forwarded-For",
        "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP"
    };

    public static bool IsAllowedName(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 128
        && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)) && !Protected.Contains(name);

    public static bool IsValidValue(string? value) => value is not null && value.Length <= 8192
        && value.All(c => c is >= ' ' and <= '~' or '\t');

    public static bool IsRequestIdName(string name) => IsAllowedName(name)
        && (name.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || name.Equals("Request-Id", StringComparison.OrdinalIgnoreCase));

    public static bool IsStatusPattern(string? pattern) => pattern == "*" || pattern is { Length: 3 }
        && pattern[0] is >= '1' and <= '5' or 'x' or 'X'
        && pattern.Skip(1).All(c => char.IsAsciiDigit(c) || c is 'x' or 'X');

    public static bool MatchesStatus(string pattern, int status)
    {
        if (!IsStatusPattern(pattern) || status is < 100 or > 599) return false;
        if (pattern == "*") return true;
        var code = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Enumerable.Range(0, 3).All(i => pattern[i] is 'x' or 'X' || pattern[i] == code[i]);
    }

    internal static void Apply(IHeaderDictionary target, IReadOnlyDictionary<string, string> defaults,
        IReadOnlyDictionary<string, string> set, IReadOnlyDictionary<string, string> append, IReadOnlyList<string> remove)
    {
        var hopHeaders = target.Connection.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Allowed(string name) => IsAllowedName(name) && !hopHeaders.Contains(name);
        foreach (var (name, value) in defaults.Take(128))
            if (Allowed(name) && IsValidValue(value) && !target.ContainsKey(name)) target[name] = value;
        foreach (var (name, value) in set.Take(128)) if (Allowed(name) && IsValidValue(value)) target[name] = value;
        foreach (var (name, value) in append.Take(128)) if (Allowed(name) && IsValidValue(value)) target.Append(name, value);
        foreach (var name in remove.Take(128)) if (Allowed(name)) target.Remove(name);
    }
}
