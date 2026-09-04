# FastCGI、uwsgi、SCGI 评估

## 当前结论

LyWaf 暂不实现 FastCGI、uwsgi 或 SCGI 上游协议，HTTP 反向代理继续以 HTTP/1.1、HTTP/2 和原生 gRPC 为边界。这三类协议需要把 HTTP 请求重新编码成各自的参数和记录格式，不是配置一个 TCP Destination 就能正确代理。

主要原因：

- FastCGI 需要管理 request ID、多路复用、PARAMS/STDIN/STDOUT/STDERR、abort 和记录分片。
- uwsgi 协议有独立变量编码、modifier 语义和部署实现差异；名称也不等于 HTTP WSGI。
- SCGI 虽较简单，仍需构建 netstring Header、CONTENT_LENGTH、半关闭和错误映射。
- 现有 YARP 的重试、熔断、Header 转换、HTTP 状态、Trailer 和可观测性都基于 HTTP；接入这些协议需要独立 Forwarder，不是新增一个 Transport 枚举即可。

## 推荐部署

应用优先直接提供 loopback 或私网 HTTP/HTTP2 端口，由 LyWaf 连接该端口。仍依赖 PHP-FPM/FastCGI 的部署可在同机使用 Nginx、Caddy 或专用应用服务器完成 FastCGI 转换，再让 LyWaf 以 HTTP 代理该本地入口：

```text
client -> LyWaf (TLS/WAF/CC) -> localhost HTTP adapter -> FastCGI application
```

适配器只监听回环地址，并限制可执行脚本根目录。LyWaf 不应把客户端 Path 直接映射为任意 `SCRIPT_FILENAME`，否则容易产生路径穿越和错误脚本执行。

## Stream 模块边界

TCP Stream 可以透明转发已经由客户端编码的 FastCGI/uwsgi/SCGI 字节流，但不会把 HTTP 转换成这些协议，也不会理解状态码、Header、请求正文或应用错误。此方式仅适用于已有协议客户端的私网四层负载，不是 Web 站点接入方案。

## 重新评估条件

只有存在无法改造为 HTTP、具有明确用户规模的遗留应用，并能提供协议兼容矩阵与安全测试样本时才重新评估。若实施，应为每个协议建立独立连接池、编码器、正文流、取消机制、错误映射、指标和模糊测试，不能复用名称相似但语义不同的实现。
