using System.Threading.Channels;
using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Plugins.Core.Testing;

/// <summary>Runs a plugin lifecycle and middleware pipeline without starting the LyWaf host.</summary>
public sealed class PluginTestHost : IAsyncDisposable
{
    private readonly ILyWafPlugin _plugin;
    private readonly TimeSpan _timeout;
    private readonly ServiceProvider _services;
    private readonly TestPluginContext _context;

    public PluginTestHost(ILyWafPlugin plugin, TimeSpan? timeout = null)
    {
        _plugin = plugin;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        var collection = new ServiceCollection();
        collection.AddLogging();
        plugin.ConfigureServices(collection, new ConfigurationBuilder().Build());
        _services = collection.BuildServiceProvider();
        _context = new TestPluginContext(_services);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _plugin.InitializeAsync(_context).WaitAsync(_timeout, cancellationToken);
        await _plugin.StartAsync(cancellationToken).WaitAsync(_timeout, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _plugin.StopAsync(cancellationToken).WaitAsync(_timeout, cancellationToken);

    public RequestDelegate BuildProxyPipeline(RequestDelegate terminal)
    {
        var builder = new ApplicationBuilder(_services);
        _plugin.ConfigureProxyPipeline(builder);
        builder.Run(terminal);
        return builder.Build();
    }

    public async Task<HttpContext> ExecuteApiAsync(string path)
    {
        if (_plugin is not IPluginApiHandler handler) throw new InvalidOperationException("插件未实现 API 处理器。");
        var context = new DefaultHttpContext { RequestServices = _services };
        var result = await handler.HandleApiAsync(path, context);
        await result.ExecuteAsync(context);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        if (_plugin.State is PluginState.Running or PluginState.Initialized)
            await StopAsync();
        await _services.DisposeAsync();
    }

    private sealed class TestPluginContext(IServiceProvider services) : IPluginContext
    {
        public IServiceProvider Services => services;
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        public NLog.Logger Logger { get; } = NLog.LogManager.GetCurrentClassLogger();
        public string DataDirectory => Path.GetTempPath();
        public IPluginTaskCoordinator Tasks => throw new NotSupportedException();
        public IPluginStorage Storage => throw new NotSupportedException();
        public T GetPluginConfig<T>(string? sectionName = null) where T : class, new() => new();
        public ILyWafPlugin? GetPlugin(string pluginId) => null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [];
        public Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : class => Task.CompletedTask;
        public IDisposable SubscribeEvent<TEvent>(Func<TEvent, Task> handler) where TEvent : class => EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Creates a seekable generated stream without allocating its declared length.</summary>
public static class PluginTestPayloads
{
    public static Stream CreateGeneratedStream(long length) => new GeneratedStream(length);

    private sealed class GeneratedStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            buffer[..count].Fill(0x5a);
            _position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset };
            return _position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Small bounded channel used by plugin tests.</summary>
public sealed class PluginTestQueue<T>
{
    private readonly Channel<T> _channel;
    public PluginTestQueue(int capacity) => _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    public bool TryWrite(T value) => _channel.Writer.TryWrite(value);
    public void Complete() => _channel.Writer.TryComplete();
    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken token = default) => _channel.Reader.ReadAllAsync(token);
}
