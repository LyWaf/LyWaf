namespace LyWaf.Services.NodeCommunication;

public sealed class PluginNodeMessageOptions
{
    public bool Enabled { get; set; }
    public Dictionary<string, string[]> Groups { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PluginNodeMessageGrant> Grants { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PluginNodeMessageGrant
{
    public string[] SendGroups { get; set; } = [];
    public string[] ReceiveGroups { get; set; } = [];
    public string[] AllowedSenders { get; set; } = [];
    public string[] RelayGroups { get; set; } = [];
}

public sealed record PluginNodeMessagePayload(string PluginId, string MessageType, int SchemaVersion,
    string TargetGroup, System.Text.Json.JsonElement Data,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OriginNodeId = null);
public sealed record PluginNodeRelayPayload(string TargetNodeId, PluginNodeMessagePayload Message);
public sealed record PluginNodeMessageReply(string PluginId, string MessageType, int SchemaVersion,
    string MessageId, string NodeId, bool Ack, string? ErrorCode = null);
