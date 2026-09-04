namespace LyWaf.Services.Acme;

/// <summary>验证按需签发域名并限制单实例签发启动频率。</summary>
internal sealed class AcmeOnDemandPolicy(AcmeOptions options)
{
    private readonly Queue<DateTimeOffset> _attempts = new();
    private readonly object _gate = new();

    public bool TryAcquire(string? domain, DateTimeOffset now, out string normalized)
    {
        normalized = NormalizeDomain(domain);
        if (!options.OnDemandEnabled || normalized.Length == 0 || !IsAllowed(normalized)) return false;
        lock (_gate)
        {
            var cutoff = now.AddHours(-1);
            while (_attempts.TryPeek(out var value) && value <= cutoff) _attempts.Dequeue();
            var limit = Math.Clamp(options.OnDemandMaxRequestsPerHour, 1, 1000);
            if (_attempts.Count >= limit) return false;
            _attempts.Enqueue(now);
            return true;
        }
    }

    internal bool IsAllowed(string domain) => options.Domains.Any(value => Matches(value, domain)) ||
        options.OnDemandAllowedDomains.Any(value => Matches(value, domain));

    internal static IReadOnlyList<Uri> GetDirectoryUris(AcmeOptions options)
    {
        var values = options.DirectoryUrls.Count > 0 ? options.DirectoryUrls : [options.DirectoryUrl];
        return values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => new Uri(value, UriKind.Absolute))
            .DistinctBy(static value => value.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsValidOptions(AcmeOptions options)
    {
        if (options.RenewBeforeDays is < 1 or > 90 || options.CheckIntervalHours is < 1 or > 168 ||
            options.OnDemandMaxRequestsPerHour is < 1 or > 1000 || options.OnDemandTimeoutSeconds is < 1 or > 120 ||
            options.InternalCertificateDays is < 1 or > 825 || options.CoordinationLockTimeoutSeconds is < 1 or > 300 ||
            options.RevocationCheckTimeoutSeconds is < 1 or > 60) return false;
        if (options.Enabled && (!options.AcceptTermsOfService || string.IsNullOrWhiteSpace(options.Email) ||
            !options.Email.Contains('@'))) return false;
        if (options.OnDemandEnabled && (!options.Enabled && !options.InternalCaEnabled ||
            options.Domains.Count == 0 && options.OnDemandAllowedDomains.Count == 0)) return false;
        if (options.Enabled && !options.UseStaging)
            try
            {
                if (GetDirectoryUris(options).Count == 0 || GetDirectoryUris(options).Any(uri => uri.Scheme != Uri.UriSchemeHttps))
                    return false;
            }
            catch (UriFormatException) { return false; }
        return !string.IsNullOrWhiteSpace(options.CertificatePath) && !string.IsNullOrWhiteSpace(options.AccountKeyPath) &&
            !string.IsNullOrWhiteSpace(options.InternalCaPath) && !string.IsNullOrWhiteSpace(options.CoordinationLockPath);
    }

    internal static string NormalizeDomain(string? value)
    {
        var domain = value?.Trim().TrimEnd('.').ToLowerInvariant() ?? "";
        if (domain.Length is 0 or > 253 || !domain.Contains('.') || Uri.CheckHostName(domain) != UriHostNameType.Dns) return "";
        return domain;
    }

    private static bool Matches(string pattern, string domain)
    {
        var normalized = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalized[1..];
            return domain.EndsWith(suffix, StringComparison.Ordinal) && domain.Length > suffix.Length;
        }
        return domain.Equals(normalized, StringComparison.Ordinal);
    }
}
