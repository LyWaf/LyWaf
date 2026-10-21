using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

internal sealed record PluginNodeAudit(string ClusterId, string SenderNodeId, string ReceiverNodeId, string MessageId,
    string PluginId, string MessageType, int SchemaVersion, string TargetGroup, string Digest,
    DateTimeOffset StartedAt, string State = "Unknown", PluginNodeMessageReply? Result = null, int? StatusCode = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OriginNodeId = null);

/// <summary>Bounded persistent receipts and audit, with a short global quota lock and per-message execution locks.</summary>
internal sealed class PluginNodeMessageJournal(string stateDirectory, string direction)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(stateDirectory), "plugin-messages", direction);
    internal static string Digest(NodeMessage message) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { message.Type, message.CorrelationId, message.Payload })));
    private string PathFor(NodeMessage message) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { message.ClusterId, message.SenderNodeId, message.ReceiverNodeId, message.MessageId }))) + ".json");

    public FileStream Lock(NodeMessage message)
    {
        Directory.CreateDirectory(_directory);
        return new(PathFor(message) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PluginNodeAudit? Read(NodeMessage message)
    {
        var path = PathFor(message);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("Plugin receipt too large");
        var record = JsonSerializer.Deserialize<PluginNodeAudit>(stream);
        if (record is null || record.ClusterId != message.ClusterId || record.SenderNodeId != message.SenderNodeId
            || record.ReceiverNodeId != message.ReceiverNodeId || record.MessageId != message.MessageId
            || record.Digest is not { Length: 64 } || !record.Digest.All(Uri.IsHexDigit)
            || (record.OriginNodeId is not null && !NodeMessageAuthentication.ValidId(record.OriginNodeId))
            || record.State is not ("Unknown" or "Completed") || (record.State == "Completed" && record.Result is null)
            || (record.State == "Unknown" && record.Result is not null)
            || (record.Result is { } result && (result.PluginId != record.PluginId || result.MessageType != record.MessageType
                || result.SchemaVersion != record.SchemaVersion || result.MessageId != record.MessageId || result.NodeId != record.ReceiverNodeId
                || (result.Ack ? result.ErrorCode is not null : result.ErrorCode is not { Length: > 0 and <= 128 }
                    || !result.ErrorCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')))))
            throw new InvalidDataException("Invalid plugin receipt");
        return record;
    }

    public void Save(NodeMessage message, PluginNodeAudit record)
    {
        using var quota = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = PathFor(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > 16384 || (!File.Exists(path) && Directory.EnumerateFiles(_directory, "*.json").Take(10000).Count() >= 10000)
            || new DirectoryInfo(_directory).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 64 * 1024 * 1024)
            throw new IOException("Plugin message journal full");
        try
        {
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(path + ".tmp", path, true);
        }
        finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
    }
}
