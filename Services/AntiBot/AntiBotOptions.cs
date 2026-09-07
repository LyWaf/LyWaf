namespace LyWaf.Services.AntiBot;

public sealed class AntiBotOptions
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Observe";
    public int ChallengeScore { get; set; } = 60;
    public int BlockScore { get; set; } = 100;
    public string ChallengePath { get; set; } = "/__lywaf/antibot/verify";
    public string CookieName { get; set; } = "__lywaf_ab";
    public string Secret { get; set; } = "";
    public int ChallengeTokenSeconds { get; set; } = 120;
    public int SessionMinutes { get; set; } = 30;
    public int ProofLeadingZeroNibbles { get; set; } = 3;
    public List<string> BypassPaths { get; set; } = ["/health", "/metrics"];
    public List<string> BypassIps { get; set; } = [];
    public string Ja4Header { get; set; } = "X-LyWaf-JA4";
    public List<string> TrustedFingerprintProxies { get; set; } = [];
}

public enum AntiBotClientClass
{
    Browser,
    ApiClient,
    SearchCrawler,
    Scanner,
    Automation,
    Unknown
}

public sealed record AntiBotAssessment(
    AntiBotClientClass ClientClass,
    int RiskScore,
    string Fingerprint,
    IReadOnlyList<string> Reasons,
    bool Bypassed,
    string? Ja4Fingerprint = null);
