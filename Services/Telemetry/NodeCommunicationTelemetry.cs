using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Hosting;

namespace LyWaf.Services.Telemetry;

public sealed record NodeCommunicationSeries(string Operation, string Direction, long Requests, long Failed,
    long Rejected, long RequestBytes, long ResponseBytes, long UnknownSizes, double TotalMilliseconds,
    long[] DurationCounts);
public sealed record NodeCommunicationSnapshot(DateTimeOffset StartedAt, DateTimeOffset CapturedAt,
    IReadOnlyList<NodeCommunicationSeries> Series, IReadOnlyDictionary<string, long> Events,
    double[] DurationBoundsMilliseconds, NodeReplicationAcknowledgementDelay? ReplicationAcknowledgement = null,
    string Scope = "process-cumulative; outbound-to-response-headers; sizes-known-content-length-or-inbound-observed");
public sealed record NodeReplicationAcknowledgementDelay(long Samples, double TotalMilliseconds, double MaxMilliseconds,
    long ClockSkewSamples, string Semantics = "mutation-change-to-transport-ack; not-business-execution-ack");

/// <summary>Fixed operation/direction labels only. Never records URLs, identities, payloads or secrets.</summary>
public sealed class NodeCommunicationTelemetry
{
    public static NodeCommunicationTelemetry Shared { get; } = new();
    private static readonly double[] Bounds = [1, 5, 10, 50, 100, 500, 1000, 5000, 15000, 60000];
    private readonly object _gate = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Dictionary<(string, string), NodeCommunicationSeries> _series = new();
    private readonly Dictionary<string, long> _events = new();
    private NodeReplicationAcknowledgementDelay _replication = new(0, 0, 0, 0);
    private static readonly Counter<long> Requests = LyWafTelemetry.Meter.CreateCounter<long>("lywaf.node.communication.requests");
    private static readonly Histogram<double> Duration = LyWafTelemetry.Meter.CreateHistogram<double>("lywaf.node.communication.duration", "ms");
    private static readonly Counter<long> Events = LyWafTelemetry.Meter.CreateCounter<long>("lywaf.node.communication.events");

    internal static string? Operation(string? path)
    {
        if (path is null) return null;
        if (path.StartsWith(NodeMessageAuthentication.Prefix, StringComparison.Ordinal))
            return Normalize(path[NodeMessageAuthentication.Prefix.Length..]);
        var prefix = path.StartsWith("/api/v1/", StringComparison.Ordinal) ? "/api/v1/" : "/api/";
        if (path == prefix + "runtime-state/agent/merge") return "runtime.merge";
        if (!path.StartsWith(prefix + "distribution/agent/", StringComparison.Ordinal)) return null;
        return Normalize(path[(prefix + "distribution/agent/").Length..] switch
        {
            "dashboard" => "node.dashboard", "status" => "node.status", "prepare" => "configuration.prepare",
            "commit" => "configuration.commit", "rollback" => "configuration.rollback", _ => "other"
        });
    }

    private static string Normalize(string operation) => NodeProtocolOperations.IsSupported(operation) ? operation : "other";

    public void Record(string operation, bool outbound, int status, double elapsed, long? requestBytes, long? responseBytes)
    {
        operation = Normalize(operation);
        var direction = outbound ? "outbound" : "inbound";
        var failed = status is < 200 or >= 400;
        lock (_gate)
        {
            var key = (operation, direction);
            var current = _series.GetValueOrDefault(key) ?? new(operation, direction, 0, 0, 0, 0, 0, 0, 0, new long[Bounds.Length + 1]);
            var counts = (long[])current.DurationCounts.Clone();
            var index = Array.FindIndex(Bounds, x => elapsed <= x);
            counts[index < 0 ? Bounds.Length : index]++;
            _series[key] = current with { Requests = current.Requests + 1, Failed = current.Failed + (failed ? 1 : 0),
                Rejected = current.Rejected + (status is 401 or 403 ? 1 : 0),
                RequestBytes = current.RequestBytes + Math.Max(0, requestBytes ?? 0),
                ResponseBytes = current.ResponseBytes + Math.Max(0, responseBytes ?? 0),
                UnknownSizes = current.UnknownSizes + (requestBytes is null || responseBytes is null ? 1 : 0),
                TotalMilliseconds = current.TotalMilliseconds + elapsed, DurationCounts = counts };
        }
        TagList tags = new() { { "operation", operation }, { "direction", direction }, { "outcome", failed ? "failed" : "ok" } };
        Requests.Add(1, tags); Duration.Record(elapsed, tags);
    }

    public void Event(string name)
    {
        if (name is not ("retry_query" or "idempotency_hit" or "signature_rejected" or "replay_rejected" or "outcome_unknown")) return;
        lock (_gate) _events[name] = _events.GetValueOrDefault(name) + 1;
        Events.Add(1, new KeyValuePair<string, object?>("event", name));
    }

    public NodeCommunicationSnapshot Capture()
    {
        lock (_gate) return new(_started, DateTimeOffset.UtcNow,
            _series.Values.Select(x => x with { DurationCounts = (long[])x.DurationCounts.Clone() }).ToArray(),
            new Dictionary<string, long>(_events), (double[])Bounds.Clone(), _replication);
    }

    /// <summary>
    /// Call only after an effective durable transport ACK, passing the original mutation change time.
    /// Includes queueing and retry time, not just local send latency; does not confirm business execution.
    /// Each call is one sample (no deduplication here). Future timestamps count as clock skew only.
    /// </summary>
    public void ObserveReplicationAcknowledgement(DateTimeOffset changedAt)
    {
        var elapsed = (DateTimeOffset.UtcNow - changedAt).TotalMilliseconds;
        lock (_gate)
        {
            if (elapsed < 0) { _replication = _replication with { ClockSkewSamples = _replication.ClockSkewSamples + 1 }; return; }
            _replication = _replication with { Samples = _replication.Samples + 1,
                TotalMilliseconds = _replication.TotalMilliseconds + elapsed, MaxMilliseconds = Math.Max(_replication.MaxMilliseconds, elapsed) };
        }
    }
}

/// <summary>Observes all HttpClient instances, including runtime clients not created by IHttpClientFactory.</summary>
public sealed class NodeCommunicationHttpObserver : IHostedService, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConditionalWeakTable<HttpRequestMessage, Started> _pending = new();
    private readonly ConcurrentBag<IDisposable> _subscriptions = new();
    public Task StartAsync(CancellationToken token) { _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this)); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken token) { foreach (var item in _subscriptions) item.Dispose(); return Task.CompletedTask; }
    public void OnNext(DiagnosticListener value)
    { if (value.Name == "HttpHandlerDiagnosticListener") _subscriptions.Add(value.Subscribe(this)); }
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Value is null) return;
        var request = value.Value.GetType().GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
        if (request is null || NodeCommunicationTelemetry.Operation(request.RequestUri?.AbsolutePath) is not { } operation) return;
        if (value.Key == "System.Net.Http.HttpRequestOut.Start")
            _pending.GetValue(request, _ => new(Stopwatch.GetTimestamp(), operation));
        else if (value.Key == "System.Net.Http.HttpRequestOut.Stop" && _pending.TryGetValue(request, out var started))
        {
            _pending.Remove(request);
            var response = value.Value.GetType().GetProperty("Response")?.GetValue(value.Value) as HttpResponseMessage;
            NodeCommunicationTelemetry.Shared.Record(started.Operation, true, response is null ? 0 : (int)response.StatusCode,
                Stopwatch.GetElapsedTime(started.At).TotalMilliseconds, request.Content?.Headers.ContentLength ?? (request.Content is null ? 0 : null),
                response?.Content.Headers.ContentLength);
        }
    }
    public void OnError(Exception error) { }
    public void OnCompleted() { }
    private sealed record Started(long At, string Operation);
}

public sealed class NodeCommunicationObservationStartup : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    { app.UseMiddleware<NodeCommunicationObservationMiddleware>(); next(app); };
}

public sealed class NodeCommunicationObservationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (NodeCommunicationTelemetry.Operation(context.Request.Path.Value) is not { } operation)
        { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        var original = context.Response.Body;
        using var observed = new ObservationStream(original);
        context.Response.Body = observed;
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            context.Response.Body = original;
            NodeCommunicationTelemetry.Shared.Record(operation, false, failed ? 0 : context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.Request.ContentLength, observed.Bytes);
            // Only small error envelopes are inspected; no body is retained, emitted or logged.
            if (context.Response.StatusCode >= 400 && observed.Bytes <= 4096)
            {
                try
                {
                    using var json = JsonDocument.Parse(observed.Prefix.ToArray());
                    if (json.RootElement.TryGetProperty("errorCode", out var code))
                    {
                        var name = code.GetString();
                        if (name is "node_invalid_signature" or "node_invalid_signature_metadata") NodeCommunicationTelemetry.Shared.Event("signature_rejected");
                        else if (name == "node_replay") NodeCommunicationTelemetry.Shared.Event("replay_rejected");
                        else if (name == "node_outcome_unknown") NodeCommunicationTelemetry.Shared.Event("outcome_unknown");
                    }
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private sealed class ObservationStream(Stream inner) : Stream
    {
        public long Bytes;
        public MemoryStream Prefix { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        private void Observe(ReadOnlySpan<byte> bytes)
        { Bytes += bytes.Length; if (Prefix.Length < 4096) Prefix.Write(bytes[..Math.Min(bytes.Length, 4096 - (int)Prefix.Length)]); }
        public override void Write(byte[] b, int o, int c) { Observe(b.AsSpan(o, c)); inner.Write(b, o, c); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken token = default)
        { Observe(b.Span); await inner.WriteAsync(b, token); }
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken token) => WriteAsync(b.AsMemory(o, c), token).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) Prefix.Dispose(); base.Dispose(disposing); }
    }
}
