using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace LyWaf.Services.ResponseWaf;

public sealed class ResponseWafOptions : IValidatableObject
{
    public bool Enabled { get; set; }
    [Range(1024, 4 * 1024 * 1024)]
    public int MaxInspectBytes { get; set; } = 256 * 1024;
    [Required, RegularExpression("^(Observe|Block|Redact)$")]
    public string Action { get; set; } = "Observe";
    [Range(400, 599)]
    public int BlockStatusCode { get; set; } = StatusCodes.Status502BadGateway;
    [StringLength(65536)]
    public string BlockMessage { get; set; } = "上游响应触发安全策略";
    public bool DetectStackTraces { get; set; } = true;
    public bool DetectDatabaseErrors { get; set; } = true;
    public bool DetectSecrets { get; set; } = true;
    public bool DetectPersonalData { get; set; }
    [Required, MaxLength(500)]
    public List<ResponseWafRule> Rules { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Rules != null && Rules.Where(rule => rule != null).Select(rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Rules.Count)
            yield return new ValidationResult("Rule IDs must be unique", [nameof(Rules)]);
    }
}

public sealed class ResponseWafRule : IValidatableObject
{
    [Required, StringLength(200)]
    public string Id { get; set; } = "custom-response-rule";
    public bool Enabled { get; set; } = true;
    [Required, RegularExpression("^(Status|Header|Body)$")]
    public string Field { get; set; } = "Body";
    [StringLength(256), RegularExpression("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    public string? HeaderName { get; set; }
    [Required, RegularExpression("^(Equal|Contains|StartsWith|EndsWith|Regex)$")]
    public string Operator { get; set; } = "Regex";
    [StringLength(4096)]
    public string Value { get; set; } = "";
    [StringLength(200)]
    public string Category { get; set; } = "自定义响应规则";

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Field == "Header" && string.IsNullOrWhiteSpace(HeaderName))
            yield return new ValidationResult("Header name is required", [nameof(HeaderName)]);
        if (Operator == "Regex" && Value != null)
        {
            bool valid;
            try { _ = new Regex(Value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); valid = true; }
            catch (ArgumentException) { valid = false; }
            if (!valid) yield return new ValidationResult("Invalid regular expression", [nameof(Value)]);
        }
    }
}

public sealed record ResponseWafFinding(string RuleId, string Category, string Field, string Evidence);
