using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NLog;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.StreamServer;

/// <summary>保持数据报边界并按客户端维护有界上游会话的 UDP 代理。</summary>
internal sealed class UdpStreamProxy(IPEndPoint listen, string listenKey, StreamConfig config,
    StreamHealthChecker? healthChecker, PrometheusMetrics? metrics = null) : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly UdpClient _listener = new(listen);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly StreamUpstreamSelector _selector = new();
    internal IPEndPoint LocalEndpoint => (IPEndPoint)_listener.Client.LocalEndPoint!;

    public async Task RunAsync(CancellationToken token)
    {
        metrics?.SetStreamUdpSessions(listenKey, 0);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var cleanup = CleanupAsync(timer, token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await _listener.ReceiveAsync(token);
                if (!StreamPrimitives.IsClientAllowed(packet.RemoteEndPoint.Address, config.AllowedNetworks, config.DeniedNetworks)) continue;
                var key = packet.RemoteEndPoint.ToString();
                if (!_sessions.TryGetValue(key, out var session))
                {
                    if (_sessions.Count >= Math.Clamp(config.MaxUdpSessions, 1, 65536))
                    {
                        metrics?.RecordStreamEvent(listenKey, "udp", "rejected");
                        continue;
                    }
                    session = await CreateSessionAsync(packet.RemoteEndPoint, token);
                    if (session is null || !_sessions.TryAdd(key, session)) { session?.Dispose(); session = _sessions.GetValueOrDefault(key); }
                    else
                    {
                        metrics?.RecordStreamEvent(listenKey, "udp", "accepted");
                        metrics?.SetStreamUdpSessions(listenKey, _sessions.Count);
                    }
                }
                if (session is null) continue;
                session.Touch();
                try
                {
                    await session.UpstreamRate.WaitAsync(packet.Buffer.Length, token);
                    await session.Upstream.SendAsync(packet.Buffer, token);
                    metrics?.AddStreamBytes(listenKey, "udp", "upstream", packet.Buffer.Length);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    healthChecker?.MarkUnhealthy(session.UpstreamName, ex.Message);
                    if (_sessions.TryRemove(key, out var failed)) failed.Dispose();
                    metrics?.RecordStreamEvent(listenKey, "udp", "connect_failed");
                    metrics?.SetStreamUdpSessions(listenKey, _sessions.Count);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            foreach (var item in _sessions.Values) item.Dispose();
            _sessions.Clear();
            try { await cleanup; } catch (OperationCanceledException) { }
        }
    }

    private async Task<Session?> CreateSessionAsync(IPEndPoint client, CancellationToken token)
    {
        var candidates = healthChecker?.GetHealthyUpstreams(config.Upstreams) ?? config.Upstreams;
        var selected = _selector.Select(candidates, config.Policy, client.Address);
        if (selected is null || !StreamPrimitives.TryParseEndpoint(selected, out var endpoint)) return null;
        try
        {
            var address = await ResolveAsync(endpoint!.Host, token);
            var udp = new UdpClient(address.AddressFamily);
            udp.Connect(address, endpoint.Port);
            var session = new Session(client, selected, udp, _selector.Track(selected), config.BytesPerSecond);
            session.ReceiveTask = RelayResponsesAsync(session, token);
            return session;
        }
        catch (Exception ex)
        {
            healthChecker?.MarkUnhealthy(selected, ex.Message);
            Logger.Warn(ex, "UDP Stream 上游会话创建失败: {Upstream}", selected);
            return null;
        }
    }

    private async Task RelayResponsesAsync(Session session, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await session.Upstream.ReceiveAsync(token);
                session.Touch();
                healthChecker?.MarkHealthy(session.UpstreamName);
                await session.DownstreamRate.WaitAsync(packet.Buffer.Length, token);
                await _listener.SendAsync(packet.Buffer, session.Client, token);
                metrics?.AddStreamBytes(listenKey, "udp", "downstream", packet.Buffer.Length);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { healthChecker?.MarkUnhealthy(session.UpstreamName, ex.Message); }
    }

    private async Task CleanupAsync(PeriodicTimer timer, CancellationToken token)
    {
        while (await timer.WaitForNextTickAsync(token))
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-Math.Clamp(config.UdpSessionTimeout, 1, 3600));
            foreach (var pair in _sessions.Where(pair => pair.Value.LastSeen < cutoff).ToArray())
                if (_sessions.TryRemove(pair.Key, out var session))
                {
                    session.Dispose();
                    metrics?.RecordStreamEvent(listenKey, "udp", "closed");
                    metrics?.SetStreamUdpSessions(listenKey, _sessions.Count);
                }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var session in _sessions.Values) session.Dispose();
        metrics?.SetStreamUdpSessions(listenKey, 0);
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        var dns = ServiceLocator.GetService<Dns.ICustomDnsService>();
        var custom = dns is null ? null : await dns.ResolveAsync(host, token);
        if (custom is not null) return custom;
        return (await System.Net.Dns.GetHostAddressesAsync(host, token)).First();
    }

    private sealed class Session(IPEndPoint client, string upstreamName, UdpClient upstream, IDisposable lease,
        long bytesPerSecond) : IDisposable
    {
        private long _lastSeen = DateTimeOffset.UtcNow.UtcTicks;
        public IPEndPoint Client { get; } = client;
        public string UpstreamName { get; } = upstreamName;
        public UdpClient Upstream { get; } = upstream;
        public DatagramRateLimiter UpstreamRate { get; } = new(bytesPerSecond);
        public DatagramRateLimiter DownstreamRate { get; } = new(bytesPerSecond);
        public Task? ReceiveTask { get; set; }
        public DateTimeOffset LastSeen => new(Interlocked.Read(ref _lastSeen), TimeSpan.Zero);
        public void Touch() => Interlocked.Exchange(ref _lastSeen, DateTimeOffset.UtcNow.UtcTicks);
        public void Dispose() { Upstream.Dispose(); lease.Dispose(); }
    }

    /// <summary>按数据报完整性串行施加平均字节速率限制。</summary>
    private sealed class DatagramRateLimiter(long bytesPerSecond)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        private long _bytes;

        public async Task WaitAsync(int bytes, CancellationToken token)
        {
            if (bytesPerSecond <= 0) return;
            await _gate.WaitAsync(token);
            try
            {
                _bytes += bytes;
                var expected = TimeSpan.FromSeconds((double)_bytes / bytesPerSecond);
                var delay = expected - _elapsed.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            }
            finally { _gate.Release(); }
        }
    }
}
