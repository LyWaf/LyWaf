using System.Text.RegularExpressions;

namespace LyWaf.Services.Protect;

/// <summary>Classifies normalized request candidates using bounded, explainable signatures.</summary>
public interface ISemanticThreatDetector
{
    ThreatFinding? Detect(RequestInspectionSnapshot snapshot, PathString path);
}

public sealed record ThreatFinding(string RuleId, string Category, string Source, string Evidence);

public sealed class SemanticThreatDetector : ISemanticThreatDetector
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Signature[] Signatures =
    [
        S("semantic-xxe", "XXE", @"<!DOCTYPE[\s\S]{0,512}<!ENTITY[\s\S]{0,512}(?:SYSTEM|PUBLIC)"),
        S("semantic-template", "模板/表达式注入", @"(?:\{\{\s*7\s*\*\s*7\s*\}\}|\$\{\s*(?:jndi:|T\s*\(|#)|<%=\s*[^%]+%>)"),
        S("semantic-ssrf-scheme", "SSRF", @"\b(?:file|gopher|dict|ftp|ldap)://"),
        S("semantic-ssrf-local", "SSRF", @"https?://(?:localhost|127(?:\.\d{1,3}){3}|0\.0\.0\.0|\[?::1\]?|169\.254\.169\.254)(?::\d+)?(?:/|$)"),
        S("semantic-traversal", "路径穿越", @"(?:\.\.[/\\]){2,}|%2e%2e(?:%2f|%5c)"),
        S("semantic-lfi", "本地文件包含", @"(?:/etc/(?:passwd|shadow|hosts)|/proc/self/(?:environ|cmdline)|\\+windows\\+win\.ini)"),
        S("semantic-rfi", "远程文件包含", @"\b(?:include|require)(?:_once)?\s*[=(]\s*https?://"),
        S("semantic-java-deser", "Java反序列化", @"(?:rO0AB|aced0005|com\.sun\.org\.apache\.xalan|org\.apache\.commons\.collections)"),
        S("semantic-php-deser", "PHP反序列化", @"(?:^|[;{}])O:\d+:""[^""\r\n]{1,128}"":\d+:\{|C:\d+:""[^""\r\n]+"":\d+:\{"),
        S("semantic-dotnet-deser", ".NET反序列化", @"(?:AAEAAAD/////|System\.Runtime\.Serialization|ObjectDataProvider|LosFormatter|__VIEWSTATE)"),
        S("semantic-nosql", "NoSQL注入", @"(?:\$(?:where|ne|gt|regex|expr)\b|\bthis\.[a-z_$][\w$]*\s*==)"),
        S("semantic-ldap", "LDAP注入", @"(?:\)\s*\([|&!]|\*\)\s*\(|\(\|\s*\([^)]*=\*)"),
        S("semantic-xpath", "XPath注入", @"(?:['""]\s+or\s+['""]?\d+['""]?\s*=|\b(?:count|string-length|substring)\s*\([^)]*\)\s*[=><])"),
    ];

    public ThreatFinding? Detect(RequestInspectionSnapshot snapshot, PathString path)
    {
        foreach (var candidate in Enumerate(snapshot, path))
        {
            if (InjectionSyntaxDetector.Detect(candidate.Value) is { } syntax)
                return new(syntax.RuleId, syntax.Category, candidate.Source,
                    candidate.Value.Length <= 160 ? candidate.Value : candidate.Value[..160]);
        foreach (var signature in Signatures)
        {
            if (!signature.Pattern.IsMatch(candidate.Value)) continue;
            return new(signature.Id, signature.Category, candidate.Source,
                candidate.Value.Length <= 160 ? candidate.Value : candidate.Value[..160]);
        }
        }
        return null;
    }

    private static IEnumerable<(string Source, string Value)> Enumerate(RequestInspectionSnapshot snapshot, PathString path)
    {
        yield return ("path", path.Value ?? "/");
        foreach (var value in snapshot.QueryValues) yield return ("query", value);
        foreach (var value in snapshot.HeaderValues) yield return ("header", value);
        foreach (var value in snapshot.BodyValues) yield return ("body", value);
    }

    private static Signature S(string id, string category, string pattern) =>
        new(id, category, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout));

    private sealed record Signature(string Id, string Category, Regex Pattern);
}

/// <summary>对注入载荷做有界词法切分，并以 Token 邻接关系判断可执行语义。</summary>
internal static class InjectionSyntaxDetector
{
    private const int MaxInput = 8192;
    private const int MaxTokens = 512;
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
        { "sh", "bash", "cmd", "powershell", "curl", "wget", "nc", "netcat", "whoami", "id", "cat" };
    private static readonly HashSet<string> CodeFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "eval", "system", "exec", "passthru", "shell_exec", "assert", "compile" };
    private static readonly HashSet<string> DelayFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "sleep", "benchmark", "pg_sleep" };

    public static (string RuleId, string Category)? Detect(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var tokens = Tokenize(input.AsSpan(0, Math.Min(input.Length, MaxInput)));
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsWord(tokens, i, "union") && (IsWord(tokens, i + 1, "select") ||
                IsWord(tokens, i + 1, "all") && IsWord(tokens, i + 2, "select")))
                return ("semantic-sqli-union-syntax", "SQL注入");
            if (IsWord(tokens, i, "un") && IsWord(tokens, i + 1, "ion") &&
                (IsWord(tokens, i + 2, "select") || IsWord(tokens, i + 2, "all") && IsWord(tokens, i + 3, "select")))
                return ("semantic-sqli-comment-syntax", "SQL注入");
            if (tokens[i].Kind == TokenKind.Quote && (IsWord(tokens, i + 1, "or") || IsWord(tokens, i + 1, "and")) &&
                IsNumber(tokens, i + 2) && IsSymbol(tokens, i + 3, "=") && IsNumber(tokens, i + 4))
                return ("semantic-sqli-boolean-syntax", "SQL注入");
            if (tokens[i].Kind == TokenKind.Word && DelayFunctions.Contains(tokens[i].Value) && IsSymbol(tokens, i + 1, "("))
                return ("semantic-sqli-delay-syntax", "SQL注入");
            if (IsWord(tokens, i, "waitfor") && IsWord(tokens, i + 1, "delay"))
                return ("semantic-sqli-delay-syntax", "SQL注入");
            if ((IsSymbol(tokens, i, ";") || IsSymbol(tokens, i, "&&") || IsSymbol(tokens, i, "||") ||
                 IsSymbol(tokens, i, "|") || IsSymbol(tokens, i, "`") || IsSymbol(tokens, i, "$(")) &&
                i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word && Commands.Contains(tokens[i + 1].Value))
                return ("semantic-command-syntax", "命令注入");
            if (tokens[i].Kind == TokenKind.Word && CodeFunctions.Contains(tokens[i].Value) && IsSymbol(tokens, i + 1, "("))
                return ("semantic-code-call-syntax", "代码注入");
            if (IsSymbol(tokens, i, "<?") && (IsWord(tokens, i + 1, "php") || IsSymbol(tokens, i + 1, "=")))
                return ("semantic-code-tag-syntax", "代码注入");
        }
        return null;
    }

    private static List<Token> Tokenize(ReadOnlySpan<char> input)
    {
        var result = new List<Token>(Math.Min(input.Length / 2, MaxTokens));
        for (var i = 0; i < input.Length && result.Count < MaxTokens;)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            if (i + 1 < input.Length && input[i] == '/' && input[i + 1] == '*')
            { var end = input[(i + 2)..].IndexOf("*/", StringComparison.Ordinal); i = end < 0 ? input.Length : i + end + 4; continue; }
            if (i + 1 < input.Length && input[i] == '-' && input[i + 1] == '-')
            { var end = input[(i + 2)..].IndexOfAny('\r', '\n'); i = end < 0 ? input.Length : i + end + 2; continue; }
            if (char.IsLetter(input[i]) || input[i] is '_' or '$')
            { var start = i++; while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] is '_' or '$' or '.')) i++; result.Add(new(TokenKind.Word, input[start..i].ToString())); continue; }
            if (char.IsDigit(input[i]))
            { var start = i++; while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++; result.Add(new(TokenKind.Number, input[start..i].ToString())); continue; }
            if (input[i] is '\'' or '"') { result.Add(new(TokenKind.Quote, input[i++].ToString())); continue; }
            var two = i + 1 < input.Length ? input.Slice(i, 2).ToString() : "";
            if (two is "&&" or "||" or "$(" or "<?") { result.Add(new(TokenKind.Symbol, two)); i += 2; }
            else result.Add(new(TokenKind.Symbol, input[i++].ToString()));
        }
        return result;
    }
    private static bool IsWord(IReadOnlyList<Token> tokens, int i, string value) => i >= 0 && i < tokens.Count &&
        tokens[i].Kind == TokenKind.Word && tokens[i].Value.Equals(value, StringComparison.OrdinalIgnoreCase);
    private static bool IsNumber(IReadOnlyList<Token> tokens, int i) => i >= 0 && i < tokens.Count && tokens[i].Kind == TokenKind.Number;
    private static bool IsSymbol(IReadOnlyList<Token> tokens, int i, string value) => i >= 0 && i < tokens.Count &&
        tokens[i].Kind == TokenKind.Symbol && tokens[i].Value == value;
    private enum TokenKind { Word, Number, Quote, Symbol }
    private sealed record Token(TokenKind Kind, string Value);
}
