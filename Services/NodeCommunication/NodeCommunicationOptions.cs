namespace LyWaf.Services.NodeCommunication;

public sealed class NodeCommunicationOptions
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "";
    public string ClusterId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Role { get; set; } = "Node";
    public string ControllerUrl { get; set; } = "";
    public string ControllerNodeId { get; set; } = "controller";
    public bool RequireHttps { get; set; } = true;
    public bool AllowLegacyProtocol { get; set; } = true;
    public string StateDirectory { get; set; } = ".lywaf-node-communication";
    public List<string> HistoricalNodeIds { get; set; } = [];
    public long MaxMessageBytes { get; set; } = 8 * 1024 * 1024;
    public int AllowedClockSkewSeconds { get; set; } = 60;
    public Dictionary<string, NodePeerOptions> Peers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class NodePeerOptions
{
    public bool Enabled { get; set; } = true;
    public string AdmissionState { get; set; } = "Active";
    public string Role { get; set; } = "Node";
    public string ActiveKeyId { get; set; } = "";
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
    public List<string> AllowedOperations { get; set; } = [];
    public List<CcCaptchaReleaseGrant> CcCaptchaReleaseGrants { get; set; } = [];
    public int CcCaptchaReleasesPerMinute { get; set; } = 30;
}

/// <summary>Delegates captcha verification only for an exact rule and canonical host:/route:/group: scope. No wildcards.</summary>
public sealed record CcCaptchaReleaseGrant(string RuleId, string Scope);

public sealed record NodeMessage(int ProtocolVersion, string ClusterId, string SenderNodeId,
    string ReceiverNodeId, string MessageId, string CorrelationId, string Type,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, System.Text.Json.JsonElement Payload);

public sealed record NodeProtocolError(string Code, int StatusCode);
