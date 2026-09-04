# BandwidthCountingMiddleware：流量统计

## 位置

它是 `MapReverseProxy` 内最外层代理中间件，包住 CORS、安全拦截、本地响应和上游代理。

## 请求阶段

进入时保存原始 `Response.Body`，再用 `CountingStream` 包装。随后调用完整下游管线。

## 响应阶段

在 `finally` 中：

1. 恢复原始 Response Body。
2. 估算请求行、Query 和请求 Header 大小。
3. 使用可 Seek Body 的 Position 或 Content-Length 计算请求体。
4. 使用 CountingStream 的实际写入量计算响应体。
5. 估算响应 Header。
6. 写入 `SharedData.Bandwidth`。

## 短路行为

它不主动短路。即使下游被黑名单、WAF 或 CORS 提前返回，它仍会统计这次响应。

## 为什么必须最外层

放在安全中间件之后会漏掉被拒绝请求；放在压缩内层可能统计压缩前内容。当前顺序记录的是最终写向客户端流的字节，更接近真实出口流量。

## 精度边界

- Body 写入量是精确计数。
- HTTP Header 是字符串长度估算，不等同于协议帧实际字节。
- HTTP/2/3 的头压缩和帧开销不会完全反映。
- 请求 Body 不可 Seek 且无 Content-Length 时可能低估。

## 排障

统计为零时检查响应是否绕过代理端口；明显偏差时区分“应用内容大小”和“网络协议实际流量”，不要把估算 Header 当作抓包结果。

## 并发与溢出

累计值必须使用线程安全更新并采用足够宽的整数类型。长时间运行或高吞吐环境需要周期性落库与归档，不能让按 IP、域名或路径的高基数键无限增长。

## 验证方法

分别请求空响应、固定小文件、压缩内容、WAF 拒绝和 Range 文件，记录统计增量并与应用层 Content-Length 对照。抓包值只用于理解估算差异，不应要求二者完全相等。
