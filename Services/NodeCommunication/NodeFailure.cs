namespace LyWaf.Services.NodeCommunication;

public sealed record NodeFailure(string Code, string? RemoteTraceId)
{
    public static NodeFailure FromException(Exception error)
    {
        var code = Identifier(error.Data["ErrorCode"] as string) ?? (error switch
        {
            OperationCanceledException => "node_timeout",
            HttpRequestException { StatusCode: not null } => "node_http_rejected",
            HttpRequestException => "node_unreachable",
            InvalidDataException or System.Text.Json.JsonException => "node_invalid_response",
            _ => "node_configuration_error"
        });
        return new(code, Identifier(error.Data["RemoteTraceId"] as string));
    }

    private static string? Identifier(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') ? value : null;
}
