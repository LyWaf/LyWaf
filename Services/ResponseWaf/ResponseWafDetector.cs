using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.ResponseWaf;

public interface IResponseWafDetector
{
    ResponseWafFinding? Detect(int statusCode, IHeaderDictionary headers, string? body);
    string Redact(string body);
}

public sealed class ResponseWafDetector(IOptionsMonitor<ResponseWafOptions> options) : IResponseWafDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex StackTrace = R(@"(?:^|\n)\s*at\s+[\w.$+<>`]+\([^\n]*\)\s+in\s+[^\n]+:\s*line\s+\d+|Traceback \(most recent call last\):");
    private static readonly Regex DatabaseError = R(@"(?:SQLSTATE\[[A-Z0-9]+\]|ORA-\d{5}|PostgreSQL.*ERROR|MySqlException|SqlException|SQLiteException|syntax error at or near)");
    private static readonly Regex Secret = R("""(?i)(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)["']?\s*[:=]\s*['"”]?([A-Za-z0-9_./+\-=]{8,})|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----""");
    private static readonly Regex Email = R(@"(?<![\w.+-])[\w.+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?![\w.-])");
    private static readonly Regex Phone = R(@"(?<!\d)1[3-9]\d{9}(?!\d)");

    public ResponseWafFinding? Detect(int statusCode, IHeaderDictionary headers, string? body)
    {
        var current = options.CurrentValue;
        foreach (var rule in current.Rules.Where(static rule => rule.Enabled))
        {
            var value = rule.Field.ToLowerInvariant() switch
            {
                "status" => statusCode.ToString(),
                "header" when !string.IsNullOrWhiteSpace(rule.HeaderName) => headers[rule.HeaderName].ToString(),
                "body" => body,
                _ => null
            };
            if (value is not null && Match(value, rule.Operator, rule.Value))
                return Finding(rule.Id, rule.Category, rule.Field, value);
        }
        if (string.IsNullOrEmpty(body)) return null;
        if (current.DetectStackTraces && StackTrace.IsMatch(body)) return Finding("response-stack-trace", "错误堆栈泄露", "Body", body);
        if (current.DetectDatabaseErrors && DatabaseError.IsMatch(body)) return Finding("response-database-error", "数据库错误泄露", "Body", body);
        if (current.DetectSecrets && Secret.IsMatch(body)) return Finding("response-secret", "密钥或凭据泄露", "Body", body);
        if (current.DetectPersonalData && (Email.IsMatch(body) || Phone.IsMatch(body))) return Finding("response-personal-data", "隐私数据泄露", "Body", body);
        return null;
    }

    public string Redact(string body)
    {
        body = Secret.Replace(body, match => match.Value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)
            ? "[REDACTED PRIVATE KEY]"
            : match.Value[..Math.Max(0, match.Value.IndexOfAny([':', '=']) + 1)] + " [REDACTED]");
        if (options.CurrentValue.DetectPersonalData)
        {
            body = Email.Replace(body, "[REDACTED EMAIL]");
            body = Phone.Replace(body, "[REDACTED PHONE]");
        }
        return body;
    }

    private static bool Match(string input, string op, string expected) => op.ToLowerInvariant() switch
    {
        "equal" => input.Equals(expected, StringComparison.OrdinalIgnoreCase),
        "contains" => input.Contains(expected, StringComparison.OrdinalIgnoreCase),
        "startswith" => input.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
        "endswith" => input.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
        "regex" => R(expected).IsMatch(input),
        _ => false
    };

    private static ResponseWafFinding Finding(string id, string category, string field, string evidence) =>
        new(id, category, field, evidence.Length <= 160 ? evidence : evidence[..160]);

    private static Regex R(string pattern) => new(pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout);
}
