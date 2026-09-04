using LyWaf.Services.WafInfo;

namespace LyWaf.Services.Protocols;

/// <summary>Kestrel 入站 HTTP/3/QUIC 的启用范围和平台失败策略。</summary>
public sealed class Http3Options
{
    /// <summary>是否尝试在 HTTPS 业务监听上启用 HTTP/3。</summary>
    public bool Enabled { get; set; }
    /// <summary>启用 HTTP/3 的 HTTPS 端口；空集合表示全部 HTTPS 业务端口。</summary>
    public int[] Ports { get; set; } = [];
    /// <summary>是否同时在 HTTPS 控制端口启用；默认关闭以减少管理面暴露。</summary>
    public bool EnableControl { get; set; }
    /// <summary>平台不支持 QUIC 时是否中止启动；false 表示记录警告并降级。</summary>
    public bool RequireSupport { get; set; } = true;
}

/// <summary>计算单个监听是否应启用 HTTP/3，集中保持协议和管理面约束。</summary>
public static class Http3Policy
{
    public static bool ShouldEnable(Http3Options options, OneLinstenInfo listen, bool isControl, bool platformSupported)
        => options.Enabled && platformSupported && listen.IsHttps && (!isControl || options.EnableControl) &&
           (options.Ports.Length == 0 || options.Ports.Contains(listen.Port));
}
