namespace LyWaf.Services.Metrics;

/// <summary>Prometheus/OpenMetrics 文本端点及代理请求标签策略。</summary>
public sealed class MetricsOptions
{
    /// <summary>是否采集 LyWaf 自定义运行指标并开放文本端点。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>控制端口上的指标路径；修改后需要重启。</summary>
    public string Path { get; set; } = "/metrics";

    /// <summary>是否为代理请求添加受配置控制的 Route ID 标签；关闭可进一步降低时序基数。</summary>
    public bool IncludeRoute { get; set; } = true;
}
