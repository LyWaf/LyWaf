# Prometheus 与 OpenMetrics 指标

LyWaf 在控制端口提供 Prometheus 文本格式指标，用于观察代理流量、请求延迟、上游健康和插件状态。指标采集不经过业务代理端口，避免把管理端点暴露给公网流量。

## 启用指标

在 `config.ly` 的全局区域加入：

```ly
control_listen = "127.0.0.1:7030"

metrics {
    enabled = true
    path = "/metrics"
    include_route = true
}
```

`enabled` 默认为 `true`。`path` 默认为 `/metrics`；修改路径后需要重启 LyWaf。`include_route` 为 `false` 时，请求指标统一使用 `route="all"`，适合路由数量很多的部署。

## 采集端点

Prometheus 配置示例：

```yaml
scrape_configs:
  - job_name: lywaf
    scrape_interval: 15s
    static_configs:
      - targets: ["127.0.0.1:7030"]
```

直接验证：

```powershell
Invoke-WebRequest http://127.0.0.1:7030/metrics | Select-Object -ExpandProperty Content
```

普通请求返回 Prometheus 0.0.4 文本。请求头接受 `application/openmetrics-text` 时返回 OpenMetrics 1.0 文本，并以 `# EOF` 结束。

该端点当前不要求控制台登录凭据，便于 Prometheus 抓取，但仅匹配 `control_listen` 端口。生产环境应让控制端口只监听管理网地址，并使用防火墙限制采集来源。

## 指标说明

| 指标 | 类型 | 标签 | 含义 |
|---|---|---|---|
| `lywaf_build_info` | Gauge | `version` | 当前构建信息 |
| `lywaf_uptime_seconds` | Gauge | 无 | 指标服务运行秒数 |
| `lywaf_http_requests_total` | Counter | `method`, `route`, `code` | 已完成的代理请求数 |
| `lywaf_http_request_duration_seconds` | Histogram | `method`, `route`, `code` | 代理请求总耗时 |
| `lywaf_http_requests_in_flight` | Gauge | `route` | 当前正在处理的代理请求数 |
| `lywaf_http_request_body_bytes_total` | Counter | `route` | 已知 `Content-Length` 的代理请求体累计字节数 |
| `lywaf_http_response_body_bytes_total` | Counter | `route` | 实际写入客户端的代理响应体累计字节数 |
| `lywaf_proxy_errors_total` | Counter | `reason` | 网关 502 与超时 504 数量 |
| `lywaf_upstream_health` | Gauge | `cluster`, `destination` | 主动健康检查状态：健康 `1`、不健康 `0`、未知 `-1` |
| `lywaf_upstream_concurrent_requests` | Gauge | `cluster`, `destination` | YARP Destination 当前并发请求数 |
| `lywaf_security_events_total` | Counter | `category` | WAF、黑名单、CC、爬虫、地域和限速事件 |
| `lywaf_security_attack_ips` | Gauge | 无 | 当前累计的唯一攻击源数量 |
| `lywaf_certificate_expiry_timestamp_seconds` | Gauge | `domain`, `source` | 当前证书到期 Unix 时间 |
| `lywaf_certificate_status` | Gauge | `domain`, `status` | 在线链/吊销检查状态 |
| `lywaf_captcha_events_total` | Counter | `action`, `result` | 验证码生成模式和验证结果 |
| `lywaf_web_tamper_queue_depth` | Gauge | 无 | 等待执行的防篡改惰性检查数 |
| `lywaf_web_tamper_queue_dropped_total` | Counter | 无 | 防篡改队列满时丢弃的检查数 |
| `lywaf_web_tamper_checks_total` | Counter | `outcome` | 防篡改检查结果 |
| `lywaf_web_tamper_check_duration_seconds` | Histogram | `outcome` | 防篡改检查耗时 |
| `lywaf_duckdb_enabled` | Gauge | 无 | DuckDB 当前是否可用 |
| `lywaf_duckdb_operations_total` | Counter | `operation`, `result` | 数据库打开、建表、刷写、查询和检查点操作数 |
| `lywaf_duckdb_operation_duration_seconds` | Histogram | `operation`, `result` | DuckDB 操作耗时 |
| `lywaf_duckdb_retention_deleted_rows_total` | Counter | `table` | 数据保留策略按表删除的行数 |
| `lywaf_plugin_state` | Gauge | `plugin`, `state` | 插件当前生命周期状态 |
| `lywaf_plugin_operations_total` | Counter | `plugin`, `operation`, `result` | 插件初始化、启动和停止的成功与失败次数 |
| `lywaf_response_cache_requests_total` | Counter | `outcome` | 响应缓存命中、未命中、旧缓存和绕过次数 |
| `lywaf_response_cache_entries` | Gauge | 无 | 当前进程维护的缓存元数据条目数 |
| `lywaf_response_cache_bytes` | Gauge | 无 | 当前节点缓存正文占用的磁盘字节数 |
| `lywaf_stream_events_total` | Counter | `listen`, `protocol`, `outcome` | Stream 接受、拒绝、连接和关闭事件 |
| `lywaf_stream_connections_active` | Gauge | `listen`, `protocol` | 当前 TCP Stream 连接数 |
| `lywaf_stream_bytes_total` | Counter | `listen`, `protocol`, `direction` | Stream 上下行负载字节数 |
| `lywaf_stream_udp_sessions` | Gauge | `listen`, `protocol` | 当前 UDP 客户端会话数 |
| `lywaf_admin_api_requests_total` | Counter | `method`, `code` | 已完成的管理 API 请求数 |
| `lywaf_admin_api_request_duration_seconds` | Histogram | `method`, `code` | 管理 API 请求耗时 |
| `lywaf_admin_api_requests_in_flight` | Gauge | 无 | 当前正在处理的管理 API 请求数 |
| `lywaf_mirror_requests_total` | Counter | `outcome` | 镜像入队、队列满、发送成功、状态失败、超时和异常数 |
| `lywaf_ext_auth_requests_total` | Counter | `outcome` | 外部授权允许、拒绝、缓存命中及失败策略结果 |
| `lywaf_ext_auth_request_duration_seconds` | Histogram | `outcome` | 外部授权决策耗时 |

请求耗时覆盖 LyWaf 代理分支中的中间件处理和上游访问。分块上传没有可信的预先长度，因此请求字节指标只累计有效 `Content-Length`；响应字节通过透传流统计实际写入量，不缓存正文。控制台 API 使用独立的 `lywaf_admin_api_*` 序列，`/metrics` 自身不计入代理请求指标。

## 标签基数

LyWaf 不使用原始 Host、Path、客户端 IP 或 User-Agent 作为指标标签。`method` 被限制为常见 HTTP 方法，状态码限制为 `100` 至 `599`，路由使用配置中的 Route ID。这样可以避免用户输入制造无限时序。

Admin API 指标有意不带请求路径：部分路径包含资源 ID，直接作为标签会形成无界时序。定位具体失败请求应使用审计日志、`X-Request-Id` 和 OpenTelemetry Trace。

如果动态生成了大量路由，设置 `include_route = false`。上游健康标签来自受控的 Cluster 和 Destination 配置，不来自请求参数。

## 告警起点

5 分钟错误率：

```promql
sum(rate(lywaf_http_requests_total{code=~"5.."}[5m]))
/
clamp_min(sum(rate(lywaf_http_requests_total[5m])), 1)
```

P95 请求延迟：

```promql
histogram_quantile(
  0.95,
  sum by (le, route) (rate(lywaf_http_request_duration_seconds_bucket[5m]))
)
```

不健康上游：

```promql
lywaf_upstream_health == 0
```

## Prometheus 与 OTLP 的统一采集

代理请求数、耗时、在途请求、请求/响应字节、上游健康/并发和 Admin API 指标由同一个采集入口产生。Prometheus/OpenMetrics 从进程内快照渲染；启用 `OpenTelemetry.ExportMetrics` 后，同一次记录也会写入 `LyWaf` Meter 并由 OTLP 导出，不会重复执行请求测量。OTLP 名称使用点分层级，例如 `lywaf.http.requests`、`lywaf.admin.request.duration` 和 `lywaf.upstream.health`。

## 当前边界

YARP 当前公开 Destination 并发请求数，但不公开底层 `SocketsHttpHandler` 连接池的实时 TCP 连接数，因此这里没有使用估算值代替。DuckDB 当前没有独立写入队列，也不导出虚构的队列深度；后续引入异步写入队列时再增加。通用插件后台队列同理，目前只有确实使用有界队列的防篡改插件提供队列指标。指标只保存在进程内，重启后 Counter 和 Histogram 从零开始，这是 Prometheus 拉取模型的正常行为。
