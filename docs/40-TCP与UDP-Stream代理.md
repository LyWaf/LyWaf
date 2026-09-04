# TCP 与 UDP Stream 代理

`StreamServer` 为数据库、缓存、邮件、DNS 和 TLS 透传等非 HTTP 流量提供四层代理。TCP 保持字节流，UDP 保持数据报边界；两者不经过 HTTP WAF 中间件。

## 基础配置

```ly
StreamServer {
    Enabled = true
    ConnectTimeout = 10
    DataTimeout = 300
    HealthCheckInterval = 10

    5432 {
        Protocol = "Tcp"
        Upstreams = ["10.0.0.11:5432", "10.0.0.12:5432"]
        Policy = "LeastConnections"
        AllowedNetworks = ["10.0.0.0/8"]
        DeniedNetworks = ["10.20.0.0/16"]
        MaxConnections = 2000
        BytesPerSecond = 0
    }

    127.0.0.1:5353 {
        Protocol = "Udp"
        Upstreams = ["10.0.0.53:53", "10.0.0.54:53"]
        Policy = "ClientIpHash"
        UdpSessionTimeout = 30
        MaxUdpSessions = 4096
    }
}
```

监听键支持端口、IPv4:端口和 `[IPv6]:端口`。上游必须写 `host:port`，IPv6 必须加方括号。非法端点、CIDR、负数资源限制、UDP+SNI、UDP+PROXY Protocol 等组合会在启动配置校验时拒绝。

## 负载策略

| 策略 | 行为 | 适用场景 |
|---|---|---|
| `RoundRobin` | 依次选择健康上游 | 默认通用策略 |
| `Random` | 随机选择 | 大量短连接 |
| `First` | 始终使用首个健康上游 | 主备顺序 |
| `LeastConnections` | 选择活动连接最少者 | 长 TCP 连接 |
| `ClientIpHash` | 对客户端 IP 做稳定散列 | 需要弱会话粘性 |

健康过滤先于负载选择。所有上游都不健康时会保留候选列表做探测性尝试，避免永久锁死。

## 健康检查

TCP 上游执行周期性连接检查；实际连接失败也累计被动失败，达到 `UnhealthyThreshold` 后暂时移出健康集合，连续成功达到 `HealthyThreshold` 后恢复。状态更新使用加锁快照，主动线程和请求线程不会丢失计数。

UDP 的 `connect()` 不代表远端服务存在，因此 UDP 上游只依据实际发送/接收异常做被动统计。若同一地址也被 TCP 配置使用，仍执行 TCP 主动检查。SNI 专属上游同样登记到健康状态表。

## PROXY Protocol

当前仅 TCP 支持 PROXY protocol v1。向上游发送：

```ly
SendProxyProtocol = true
```

接受下游 PROXY 头时必须设置可信直接代理 CIDR：

```ly
AcceptProxyProtocol = true
ProxyProtocolTrustedNetworks = ["10.0.0.0/24"]
AllowedNetworks = ["203.0.113.0/24"]
```

LyWaf 先按 socket 对端校验 `ProxyProtocolTrustedNetworks`，再按 PROXY 头声明的源地址执行业务 `AllowedNetworks/DeniedNetworks`。空可信列表会使配置启动失败，避免公网客户端伪造来源。v1 头严格限制为 108 字节。

## TLS SNI 透传

```ly
SniPreread = true
SniUpstreams {
    api.example.com = ["10.0.0.21:443"]
    "*.example.com" = ["10.0.0.22:443"]
}
```

LyWaf 最多预读一个 64 KiB TLS handshake record，仅解析 ClientHello 的明文 host_name，不终止或解密 TLS。精确域名优先，其次最长通配后缀，最后回退到 `Upstreams`。预读内容会原样发送给选中的上游。跨多个 TLS record 的极端 ClientHello 无法提取 SNI，但流量仍回退并透传。

## 访问控制与限速

`DeniedNetworks` 优先于 `AllowedNetworks`；允许列表为空表示允许所有未被拒绝的地址。`MaxConnections` 限制单个 TCP 监听器并发连接，0 表示不限。`BytesPerSecond` 对 TCP 连接或 UDP 客户端会话的上下行分别按平均速率限速，0 表示不限；UDP 等待限速时保持整个数据报，不会拆包。

UDP 按客户端 IP:端口建立上游会话并固定选择结果。`MaxUdpSessions` 硬限制状态表，`UdpSessionTimeout` 清理空闲 socket，单会话失败不会终止监听器。

## 指标

- `lywaf_stream_events_total{listen,protocol,outcome}`：接受、拒绝、连接成功/失败和关闭。
- `lywaf_stream_connections_active{listen,protocol}`：TCP 当前活动连接。
- `lywaf_stream_bytes_total{listen,protocol,direction}`：上下行负载字节。
- `lywaf_stream_udp_sessions{listen,protocol="udp"}`：UDP 当前会话数。

指标不使用客户端 IP、SNI 或上游地址作为标签。自动测试位于 `StreamPrimitivesTests.cs` 和 `StreamRuntimeTests.cs`，包含真实 loopback TCP、UDP、PROXY 与 SNI 数据路径。
