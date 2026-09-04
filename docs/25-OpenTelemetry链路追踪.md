# OpenTelemetry 链路追踪

LyWaf 可通过 OTLP 导出入口请求、YARP 上游 HttpClient、插件生命周期和 DuckDB 查询遥测。功能默认关闭，不会改变未配置部署的网络行为。

## 配置

```ly
otel {
    enabled = true
    service_name = "lywaf-edge"
    endpoint = "http://127.0.0.1:4317"
    protocol = "grpc"
    sample_ratio = 0.1
    export_traces = true
    export_metrics = true
    excluded_paths = ["/metrics", "/health"]
}
```

`protocol` 支持 `grpc` 和 `http/protobuf`。使用 `http/protobuf` 时通常把端点配置为 Collector 的 HTTP OTLP 基地址，例如 `http://127.0.0.1:4318`。采样率会限制到 `0..1`，并使用 ParentBased 采样：已有上游采样决定时继续遵循该决定。

## Trace Context

.NET 进程启用 W3C Activity ID，ASP.NET Core instrumentation 自动读取和传播 `traceparent`、`tracestate`。YARP 发起上游请求时，HttpClient instrumentation 自动注入同一条链路的上下文。

主要 Span：

- ASP.NET Core 入口请求。
- `lywaf.proxy.pipeline` 核心代理中间件管线。
- YARP 使用的 HttpClient 上游请求。
- `plugin.initialize`、`plugin.start` 和 `plugin.stop`。
- `duckdb.query`。

## 敏感信息

入站和出站 URL 标签都会移除查询字符串。LyWaf 不主动写入 Cookie、Authorization、请求体、客户端 IP 或网页防篡改内容。新增自定义 Span 时也不得把这些值作为属性。

## 故障隔离

OTLP 使用 SDK 的批量导出器。Collector 不可达时，导出在后台失败，不会让代理请求失败。生产环境仍应监控 Collector 和 LyWaf 日志，并根据出口带宽调整采样率。

## 验证

将 Collector 的 OTLP 接收地址写入配置并启动 LyWaf，访问一个代理路由。Trace 中应先看到 ASP.NET Core 服务端 Span，再看到 HttpClient 客户端 Span；请求携带已有 `traceparent` 时，LyWaf Span 应连接到该父链路。

启用 `export_metrics` 后，`LyWaf` Meter 同时导出代理请求数、耗时、在途请求、请求/响应字节、上游健康/并发以及 Admin API 请求指标。这些测量与 `/metrics` 使用同一个采集入口，但 OTLP 遵循点分命名，例如 `lywaf.http.requests`；Collector 后端可能按自身规则转换名称和单位。
