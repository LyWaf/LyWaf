using System.Globalization;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace LyWaf.Localization;

/// <summary>Shared display resources. Protocol keys and configuration values are never translated.</summary>
public static class TextLocalizer
{
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> Catalogs =
        new[] { "zh-CN", "en-US" }.ToDictionary(language => language, language =>
        {
            using var stream = typeof(TextLocalizer).Assembly.GetManifestResourceStream($"LyWaf.Localization.{language}.json")
                ?? throw new InvalidOperationException($"Missing localization resource: {language}");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        });
    private static readonly Dictionary<string, string> DisplayKeys = Catalogs["zh-CN"]
        .Where(pair => !pair.Value.Contains('{')).GroupBy(pair => pair.Value)
        .ToDictionary(group => group.Key, group => group.First().Key);

    public static string Normalize(string? language) =>
        language?.Equals("en", StringComparison.OrdinalIgnoreCase) == true ||
        language?.StartsWith("en-", StringComparison.OrdinalIgnoreCase) == true ? "en-US" : "zh-CN";

    public static bool HasKey(string key) => Catalogs["zh-CN"].ContainsKey(key);
    public static string Get(string key) => Catalogs[Normalize(CultureInfo.CurrentUICulture.Name)].GetValueOrDefault(key)
        ?? Catalogs["zh-CN"].GetValueOrDefault(key) ?? key;
    public static string Display(string text) => DisplayKeys.TryGetValue(text, out var key) ? Get(key) : text;

    public static string ResolveLanguage(string? header, string? fallback = "zh-CN")
    {
        if (StringWithQualityHeaderValue.TryParseList(new[] { header ?? "" }, out var values))
            foreach (var value in values.OrderByDescending(value => value.Quality ?? 1))
            {
                if (value.Quality == 0) continue;
                var name = value.Value.ToString();
                if (name.Equals("en", StringComparison.OrdinalIgnoreCase) || name.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) return "en-US";
                if (name.Equals("zh", StringComparison.OrdinalIgnoreCase) || name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            }
        return Normalize(fallback);
    }

    public static IApplicationBuilder UseManagementLocalization(this IApplicationBuilder app, int controlPort, string? defaultLanguage) =>
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort != controlPort) { await next(); return; }
            var language = ResolveLanguage(context.Request.Headers.AcceptLanguage, defaultLanguage);
            var previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.ContentLanguage = language;
                context.Response.Headers.Append("Vary", "Accept-Language");
                return Task.CompletedTask;
            });
            try { await next(); }
            finally { CultureInfo.CurrentUICulture = previous; }
        });
}
