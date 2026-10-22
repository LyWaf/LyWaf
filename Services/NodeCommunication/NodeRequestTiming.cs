using System.Diagnostics;

namespace LyWaf.Services.NodeCommunication;

internal sealed record NodeRequestTiming(DateTimeOffset ReceivedAt, long Started)
{
    private static readonly object Key = new();
    internal static void Capture(HttpContext context) => context.Items.TryAdd(Key, new NodeRequestTiming(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()));
    internal static NodeRequestTiming? Read(HttpContext context) => context.Items.TryGetValue(Key, out var value) ? value as NodeRequestTiming : null;
}
