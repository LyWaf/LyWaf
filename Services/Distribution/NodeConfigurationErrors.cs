namespace LyWaf.Services.Distribution;

public sealed class NodeConfigurationOutcomeUnknownException() : IOException("Configuration outcome requires reconciliation");

public static class NodeConfigurationErrors
{
    public const string Invalid = "node_config_invalid";
    public const string Digest = "node_config_digest_mismatch";
    public const string Conflict = "node_config_conflict";
    public const string Expired = "node_config_expired";
    public const string State = "node_config_state_invalid";
    public const string Missing = "node_config_candidate_missing";
    public const string Storage = "node_config_storage_unavailable";
    public const string Unknown = "node_config_outcome_unknown";

    public static bool IsKnown(string? code) => code is Invalid or Digest or Conflict or Expired or State or Missing or Storage or Unknown;

    // Unknown is never a definitive business NACK. It requires read-only reconciliation.
    public static int HttpStatus(string? code) => code switch
    {
        Invalid or Digest => 422,
        Conflict or Expired or State or Missing or Storage => 409,
        _ => 503
    };
}
