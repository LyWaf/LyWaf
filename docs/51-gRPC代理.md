# gRPC 代理

LyWaf 通过 Kestrel HTTP/2 入口和 YARP HTTP/2 上游连接透明代理原生 gRPC。它不解析 Protobuf 消息，也不生成服务存根，因此普通 unary、client streaming、server streaming 和 duplex streaming 都保持端到端语义。

## TLS 入口

HTTPS 监听默认同时支持 HTTP/1.1 和 HTTP/2。为 Cluster 强制上游 HTTP/2：

```ly
https://grpc.example.com {
    proxy {
        to = "http://127.0.0.1:5000"
        grpc = true
    }
}
```

`grpc=true` 写入 YARP Cluster 的 `HttpRequest.Version=2.0` 和 `VersionPolicy=RequestVersionExact`。上游不支持 HTTP/2 时请求失败，不会静默降级到 HTTP/1.1，因为原生 gRPC 不能在 HTTP/1.1 上工作。

## 明文 h2c 入口

```ly
Grpc {
    EnableH2c = true
    H2cPorts = [8080]
}

:8080 {
    proxy {
        to = "http://127.0.0.1:5000"
        grpc = true
    }
}
```

`H2cPorts` 必须显式列出，每个端口被配置为 HTTP/2 prior-knowledge only。无 TLS 时没有 ALPN，Kestrel 不能在同一端口可靠地区分普通 HTTP/1.1 和 h2c，因此不要把已有 REST HTTP 端口直接加入该列表。控制端口不会启用 h2c。

## 中间件与流式边界

- WAF 默认不读取 gRPC Protobuf 正文；启用通用 POST 正文规则前需要评估二进制误报和缓冲成本。
- `application/grpc` 不在响应压缩 MIME 列表中，gRPC 自己的 `grpc-encoding` 不会被 HTTP 压缩中间件替代。
- 请求镜像默认不允许 POST，因此不会复制 gRPC 调用；强行加入 POST 仍受已知 `Content-Length` 限制，不适合流式 RPC。
- 外部授权可以基于 Header 和 Path 决策。流式或未知长度正文只发送有限视图，不应依赖完整 Protobuf Body 授权。
- 请求排空会等待仍在进行的 gRPC 流直到全局排空超时；长连接需要与升级窗口协调。

## 当前不支持

LyWaf 当前不提供 gRPC-Web 转码、JSON transcoding、Protobuf 字段级 WAF、服务反射代理配置或按 `grpc-status` 的专用主动健康检查。YARP 会转发 HTTP/2 Header、正文和 Trailer，但通用 HTTP 健康检查主要判断连接和 HTTP 状态；需要语义健康检查时应由独立 HTTP 健康端点或后续专用 gRPC Health Checking Protocol 实现。

浏览器直接调用通常需要 gRPC-Web，这与原生 gRPC 透明代理是不同能力，不能仅靠开启 CORS 解决。

## 验证

使用 `grpcurl` 或真实客户端调用 unary 与双向流式方法，确认响应 Trailer 中的 `grpc-status` 正确；再停止一个上游，验证健康检查、重试限制和流取消。h2c 测试必须使用明确支持 prior knowledge 的客户端，普通浏览器访问失败是预期行为。
