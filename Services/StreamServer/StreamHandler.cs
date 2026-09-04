using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NLog;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.StreamServer;

/// <summary>处理单个 TCP Stream 连接的预读、选路和双向转发。</summary>
public sealed class StreamHandler
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly StreamServerOptions _globalOptions;
    private readonly StreamConfig _streamConfig;
    private readonly StreamHealthChecker? _healthChecker;
    private readonly string _listenKey;
    private readonly StreamUpstreamSelector _selector = new();
    private readonly PrometheusMetrics? _metrics;

    public StreamHandler(StreamServerOptions globalOptions, StreamConfig streamConfig, string listenKey,
        StreamHealthChecker? healthChecker = null, PrometheusMetrics? metrics = null)
    {
        _globalOptions = globalOptions;
        _streamConfig = streamConfig;
        _listenKey = listenKey;
        _healthChecker = healthChecker;
        _metrics = metrics;
    }

    /// <summary>转发一个已经接受的 TCP Socket，直至任一方向结束或取消。</summary>
    public async Task HandleAsync(Socket clientSocket, CancellationToken cancellationToken = default)
    {
        if (_streamConfig.Upstreams.Count == 0) return;
        var peer = clientSocket.RemoteEndPoint as IPEndPoint;
        var local = clientSocket.LocalEndPoint as IPEndPoint;
        if (peer is null || local is null) return;
        if (!_streamConfig.AcceptProxyProtocol && !StreamPrimitives.IsClientAllowed(peer.Address,
                _streamConfig.AllowedNetworks, _streamConfig.DeniedNetworks)) return;

        using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);
        var effectivePeer = peer;
        byte[] prefix = [];
        string? serverName = null;
        if (_streamConfig.AcceptProxyProtocol)
        {
            if (_streamConfig.ProxyProtocolTrustedNetworks.Count == 0 ||
                !StreamPrimitives.IsClientAllowed(peer.Address, _streamConfig.ProxyProtocolTrustedNetworks, []))
            {
                Logger.Warn("Stream {Listen} 拒绝不可信 PROXY 对端 {Peer}", _listenKey, peer.Address);
                return;
            }
            var proxyLine = await ReadUntilCrlfAsync(clientStream, ProxyProtocolV1.MaxHeaderBytes, cancellationToken);
            if (proxyLine is null || !ProxyProtocolV1.TryParse(proxyLine, out var declaredPeer, out _, out _)) return;
            if (declaredPeer is not null) effectivePeer = declaredPeer;
            if (!StreamPrimitives.IsClientAllowed(effectivePeer.Address, _streamConfig.AllowedNetworks, _streamConfig.DeniedNetworks)) return;
        }
        if (_streamConfig.SniPreread)
        {
            prefix = await ReadTlsRecordAsync(clientStream, cancellationToken) ?? [];
            TlsClientHelloParser.TryGetServerName(prefix, out serverName);
        }

        var candidates = StreamPrimitives.ResolveSniUpstreams(_streamConfig, serverName);
        candidates = _healthChecker?.GetHealthyUpstreams(candidates) ?? candidates;
        var selected = _selector.Select(candidates, _streamConfig.Policy, effectivePeer.Address);
        if (selected is null || !StreamPrimitives.TryParseEndpoint(selected, out var endpoint)) return;

        Socket? targetSocket = null;
        var upstreamConnected = false;
        try
        {
            var address = await ResolveAsync(endpoint!.Host, cancellationToken);
            targetSocket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(_streamConfig.ConnectTimeout ?? _globalOptions.ConnectTimeout));
            await targetSocket.ConnectAsync(new IPEndPoint(address, endpoint.Port), connectCts.Token);
            upstreamConnected = true;
            _healthChecker?.MarkHealthy(selected);
            _metrics?.RecordStreamEvent(_listenKey, "tcp", "connected");
            _metrics?.ChangeStreamActive(_listenKey, "tcp", 1);
            using var lease = _selector.Track(selected);
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);
            try
            {
                if (_streamConfig.SendProxyProtocol)
                {
                    var line = Encoding.ASCII.GetBytes(ProxyProtocolV1.Create(effectivePeer, local));
                    await targetStream.WriteAsync(line, cancellationToken);
                }
                if (prefix.Length > 0) await targetStream.WriteAsync(prefix, cancellationToken);
                await TunnelAsync(clientStream, targetStream, cancellationToken);
            }
            finally { _metrics?.ChangeStreamActive(_listenKey, "tcp", -1); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!upstreamConnected)
            {
                _healthChecker?.MarkUnhealthy(selected, ex.Message);
                _metrics?.RecordStreamEvent(_listenKey, "tcp", "connect_failed");
            }
            Logger.Warn(ex, "Stream {Listen} -> {Upstream} 转发失败", _listenKey, selected);
        }
        finally { targetSocket?.Dispose(); }
    }

    private async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        var dns = ServiceLocator.GetService<Dns.ICustomDnsService>();
        var custom = dns is null ? null : await dns.ResolveAsync(host, cancellationToken);
        if (custom is not null) return custom;
        return (await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)).First();
    }

    private async Task TunnelAsync(Stream client, Stream target, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromSeconds(_streamConfig.DataTimeout ?? _globalOptions.DataTimeout);
        var first = CopyAsync(client, target, timeout, _streamConfig.BytesPerSecond,
            bytes => _metrics?.AddStreamBytes(_listenKey, "tcp", "upstream", bytes), linked.Token);
        var second = CopyAsync(target, client, timeout, _streamConfig.BytesPerSecond,
            bytes => _metrics?.AddStreamBytes(_listenKey, "tcp", "downstream", bytes), linked.Token);
        var completed = await Task.WhenAny(first, second);
        await linked.CancelAsync();
        try { await Task.WhenAll(first, second); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        await completed;
    }

    private static async Task CopyAsync(Stream source, Stream destination, TimeSpan timeout, long bytesPerSecond,
        Action<int> observeBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        var stopwatch = Stopwatch.StartNew();
        long transferred = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(timeout);
            var read = await source.ReadAsync(buffer, idle.Token);
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), idle.Token);
            observeBytes(read);
            transferred += read;
            if (bytesPerSecond > 0)
            {
                var expected = TimeSpan.FromSeconds((double)transferred / bytesPerSecond);
                var delay = expected - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static async Task<byte[]?> ReadUntilCrlfAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        var buffer = new byte[maxBytes];
        for (var count = 0; count < buffer.Length; count++)
        {
            if (await stream.ReadAsync(buffer.AsMemory(count, 1), token) != 1) return null;
            if (count > 0 && buffer[count - 1] == '\r' && buffer[count] == '\n') return buffer[..(count + 1)];
        }
        return null;
    }

    private static async Task<byte[]?> ReadTlsRecordAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        if (!await ReadExactlyAsync(stream, header, token)) return null;
        var required = TlsClientHelloParser.RequiredRecordBytes(header);
        if (required < 5) return header;
        var result = new byte[required];
        header.CopyTo(result, 0);
        return await ReadExactlyAsync(stream, result.AsMemory(5), token) ? result : null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
