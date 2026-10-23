using System.ComponentModel.DataAnnotations;
using LyWaf.Plugins.Core;

namespace LyWaf.Plugins.Sys.HttpSettings;

public class HttpSettingsOptions : IValidatableObject
{
    [PluginConfiguration(LabelKey = "config.Enabled.label")] public bool Enabled { get; set; } = true;
    [PluginConfiguration(LabelKey = "config.RequestHeaders.label")] public Dictionary<string, string> RequestHeaders { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.RequestHeadersIfMissing.label")] public Dictionary<string, string> RequestHeadersIfMissing { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.AppendRequestHeaders.label")] public Dictionary<string, string> AppendRequestHeaders { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.RemoveRequestHeaders.label")] public List<string> RemoveRequestHeaders { get; set; } = [];
    // Existing Headers retains its add-only-if-absent semantics.
    [PluginConfiguration(LabelKey = "config.Headers.label")]
    public Dictionary<string, string> Headers { get; set; } = new() { ["X-Powered-By"] = "LyWaf", ["X-Frame-Options"] = "SAMEORIGIN" };
    [PluginConfiguration(LabelKey = "config.ResponseHeaders.label")] public Dictionary<string, string> ResponseHeaders { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.AppendResponseHeaders.label")] public Dictionary<string, string> AppendResponseHeaders { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.RemoveResponseHeaders.label")] public List<string> RemoveResponseHeaders { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.StatusCodes.label", DescriptionKey = "config.StatusCodes.description")]
    public List<string> StatusCodes { get; set; } = [];
    [PluginConfiguration(LabelKey = "config.SecurityHeaders.label")] public bool SecurityHeaders { get; set; }
    [PluginConfiguration(LabelKey = "config.HstsMaxAgeSeconds.label"), Range(0, 63072000)] public int HstsMaxAgeSeconds { get; set; }
    [PluginConfiguration(LabelKey = "config.RequestIdHeader.label", DescriptionKey = "config.RequestIdHeader.description")]
    public string RequestIdHeader { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, values) in Maps())
            if (values is null || values.Count > 128 || values.Any(p => !HttpHeaderPolicy.IsAllowedName(p.Key)
                || !HttpHeaderPolicy.IsValidValue(p.Value))) yield return new("Invalid HTTP header policy", [name]);
        foreach (var (name, values) in Lists())
            if (values is null || values.Count > 128 || values.Any(v => !HttpHeaderPolicy.IsAllowedName(v)))
                yield return new("Invalid HTTP header names", [name]);
        if (StatusCodes is null || StatusCodes.Count > 64 || StatusCodes.Any(code => !HttpHeaderPolicy.IsStatusPattern(code)))
            yield return new("Invalid HTTP status pattern", [nameof(StatusCodes)]);
        if (RequestIdHeader is null || RequestIdHeader.Length > 128
            || RequestIdHeader.Length > 0 && !HttpHeaderPolicy.IsRequestIdName(RequestIdHeader))
            yield return new("Invalid request ID header", [nameof(RequestIdHeader)]);
    }

    internal IEnumerable<(string Name, Dictionary<string, string> Values)> Maps()
    {
        yield return (nameof(Headers), Headers); yield return (nameof(RequestHeaders), RequestHeaders);
        yield return (nameof(RequestHeadersIfMissing), RequestHeadersIfMissing); yield return (nameof(AppendRequestHeaders), AppendRequestHeaders);
        yield return (nameof(ResponseHeaders), ResponseHeaders); yield return (nameof(AppendResponseHeaders), AppendResponseHeaders);
    }
    internal IEnumerable<(string Name, List<string> Values)> Lists()
    { yield return (nameof(RemoveRequestHeaders), RemoveRequestHeaders); yield return (nameof(RemoveResponseHeaders), RemoveResponseHeaders); }
}
