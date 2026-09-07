namespace LyWaf.Services.ResponseWaf;

public sealed class ResponseWafOptions
{
    public bool Enabled { get; set; }
    public int MaxInspectBytes { get; set; } = 256 * 1024;
    public string Action { get; set; } = "Observe";
    public int BlockStatusCode { get; set; } = StatusCodes.Status502BadGateway;
    public string BlockMessage { get; set; } = "上游响应触发安全策略";
    public bool DetectStackTraces { get; set; } = true;
    public bool DetectDatabaseErrors { get; set; } = true;
    public bool DetectSecrets { get; set; } = true;
    public bool DetectPersonalData { get; set; }
    public List<ResponseWafRule> Rules { get; set; } = [];
}

public sealed class ResponseWafRule
{
    public string Id { get; set; } = "custom-response-rule";
    public bool Enabled { get; set; } = true;
    public string Field { get; set; } = "Body";
    public string? HeaderName { get; set; }
    public string Operator { get; set; } = "Regex";
    public string Value { get; set; } = "";
    public string Category { get; set; } = "自定义响应规则";
}

public sealed record ResponseWafFinding(string RuleId, string Category, string Field, string Evidence);
