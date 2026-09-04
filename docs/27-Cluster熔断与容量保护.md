# Cluster 熔断与容量保护

Cluster 容量保护限制单个上游集群消耗的连接和并发资源。当活动请求、等待队列或并发重试达到上限时，LyWaf 快速返回 `503`，避免故障上游拖垮代理进程。

## 配置

```ly
api.example.com {
    proxy {
        to = ["http://10.0.0.11:8080", "http://10.0.0.12:8080"]
        circuit_breaker {
            Enabled = true
            MaxConnections = 200
            MaxActiveRequests = 1000
            MaxQueuedRequests = 500
            MaxConcurrentRetries = 100
        }
    }
}
```

配置键同时支持 PascalCase、snake_case 和 kebab-case。

| 配置 | 作用 |
|---|---|
| `MaxConnections` | `SocketsHttpHandler` 对每个上游服务器维护的最大物理连接数 |
| `MaxActiveRequests` | Cluster 同时处理的逻辑请求上限 |
| `MaxQueuedRequests` | 活动槽位用尽后允许等待的请求数；设为 `0` 时立即拒绝 |
| `MaxConcurrentRetries` | Cluster 内同时进行的额外重试上限 |

队列等待服从客户端请求取消。客户端断开后不会继续占用等待槽位。限制按 YARP Cluster 隔离，配置热更新创建新客户端和新的容量状态。

## 与重试配合

Retry Budget 限制一段时间内允许产生多少额外请求；`MaxConcurrentRetries` 限制同一时刻有多少重试正在退避或访问上游。两者应同时配置，前者抑制总量，后者抑制瞬时并发。

## 指标

- `lywaf_circuit_breaker_state`：`0` 正常、`1` 饱和、`2` 拒绝发生。
- `lywaf_circuit_breaker_capacity{kind="remaining|queued|active|retries"}`：当前容量快照。
- `lywaf_circuit_breaker_rejections_total{reason="queue_full|retry_limit"}`：按原因累计拒绝数。

状态指标是最近一次容量变化的快照，告警应优先基于拒绝计数增长率，而不是对瞬时状态单独告警。

## 参数起点

先根据上游实例的线程池、数据库连接池和实测吞吐确定 `MaxActiveRequests`，再设置不超过短时间可接受延迟的队列。连接数不是越大越好；HTTP/2 可在少量连接上复用请求。生产发布前应通过并发压测验证 503 比例、排队时延和恢复速度。
