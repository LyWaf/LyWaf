using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

/// <summary>Validates response header names and values before configuration is published.</summary>
public sealed class ResponseHeadersAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        if (value is not Dictionary<string, string> headers || headers.Count > 100) return false;
        return headers.All(header => header.Key.Length is > 0 and <= 256
            && header.Key.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))
            && header.Value is { Length: <= 8192 }
            && header.Value.All(c => c == '\t' || (c >= ' ' && c != '\u007f')));
    }
}
