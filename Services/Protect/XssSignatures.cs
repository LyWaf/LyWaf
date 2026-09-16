namespace LyWaf.Services.Protect;

/// <summary>Shared bounded XSS signatures for semantic findings and configurable system rules.</summary>
internal static class XssSignatures
{
    public const string Pattern = @"(?:<\s*(?:script|iframe)\b|<\s*[a-z][\w:-]*\b[^<>]{0,2048}[\s/]+on(?:error|load|focus(?:in|out)?|blur|click|dblclick|mouse[a-z]*|key(?:down|up|press)|submit|change|input|pointer[a-z]*|animation[a-z]*|transition[a-z]*|toggle|beforetoggle|begin|end|repeat)\s*=|\bjavascript\s*:)";
}
