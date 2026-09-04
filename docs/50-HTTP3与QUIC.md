# HTTP/3 与 QUIC

LyWaf 使用 .NET 9 Kestrel 的原生 HTTP/3 实现，在同一个 HTTPS 业务端口同时提供 HTTP/1.1、HTTP/2 和 HTTP/3。LyWaf 不实现自有 QUIC 协议栈。

## 配置

```ly
Http3 {
    Enabled = true
    Ports = [443, 8443]
    EnableControl = false
    RequireSupport = true
}
```

`Ports` 为空时作用于全部 HTTPS 业务监听，否则只作用于列出的端口。HTTP/3 必须使用 TLS，因此 HTTP 监听永远不会启用。控制端口默认排除；只有明确设置 `EnableControl=true` 且控制监听本身为 HTTPS 时才开放。

`RequireSupport=true` 时，配置启用但 `QuicConnection.IsSupported=false` 会中止启动，避免部署误以为已经提供 HTTP/3。设为 false 时记录警告并继续提供 HTTP/1.1 与 HTTP/2。

## 部署条件

- 操作系统、.NET 运行时及其 QUIC/MsQuic 依赖必须支持 `System.Net.Quic`。
- TCP 和 UDP 必须同时放行相同 HTTPS 端口；只开放 TCP 时客户端无法建立 QUIC。
- HTTPS 监听必须能选择有效证书，证书域名、有效期和信任链要求与 HTTP/2 相同。
- NAT、负载均衡器和云安全组都必须保留 UDP；多层入口中的每一层都要检查。

Kestrel 会处理协议协商和 Alt-Svc。客户端不支持 HTTP/3、UDP 不通或 QUIC 建连失败时，通常继续使用 HTTP/2/1.1；是否自动回退由客户端实现决定。

## 边界

该功能只改变客户端到 LyWaf 的入站协议。LyWaf 到业务上游仍使用当前 YARP/HttpClient 的 HTTP/1.1 或 HTTP/2 连接策略，不承诺上游 HTTP/3。Stream UDP 转发也不是 HTTP/3 终止，两者配置和指标独立。

监听协议属于 Kestrel 启动结构，修改 `Http3`、HTTPS 端口或控制端口需要重启，不能依赖普通配置热更新。0-RTT、QUIC 连接迁移、HTTP/3 Datagram 和 WebTransport 当前不作为 LyWaf 公共能力承诺。

## 验证

启动日志不应出现 QUIC 降级警告。使用支持 HTTP/3 的客户端访问 HTTPS 业务端口，并在抓包或客户端协议输出中确认 `HTTP/3`，同时验证同一端口仍可由 HTTP/2 客户端访问。生产监控应分别观察应用请求错误和主机 UDP 丢包；仅看到 TCP 443 正常不能证明 HTTP/3 可用。
