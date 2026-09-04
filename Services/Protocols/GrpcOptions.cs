using Microsoft.AspNetCore.Server.Kestrel.Core;
using LyWaf.Services.WafInfo;

namespace LyWaf.Services.Protocols;

/// <summary>gRPC 明文 HTTP/2（h2c）入口配置；TLS 监听原生支持 HTTP/2，无需列入此处。</summary>
public sealed class GrpcOptions
{
    /// <summary>是否允许在明确列出的非 TLS 业务端口启用 h2c。</summary>
    public bool EnableH2c { get; set; }
    /// <summary>仅接受 HTTP/2 prior-knowledge 的明文端口。</summary>
    public int[] H2cPorts { get; set; } = [];
}

/// <summary>集中计算监听协议，避免误把普通 HTTP 端口切成 h2c-only。</summary>
public static class GrpcProtocolPolicy
{
    public static HttpProtocols ForHttpListener(GrpcOptions options, OneLinstenInfo listen, bool isControl)
        => options.EnableH2c && !isControl && !listen.IsHttps && options.H2cPorts.Contains(listen.Port)
            ? HttpProtocols.Http2
            : HttpProtocols.Http1AndHttp2;
}
