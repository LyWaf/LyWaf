# ThrottledMiddleware：响应带宽节流

## 位置

位于 CcProtection 后、SpeedLimit 前。它包装 Response Body，控制响应写出速度，但不限制每秒请求数量。

## 配置选择顺序

```text
匹配路径 PathLimits
    ↓ 未配置
匹配客户端 IP IpLimits
    ↓ 未配置
Global
```

配置值按 KB/s 转成字节每秒。没有任何有效限制时不包装响应流。

## UrlThrottledStream

用于路径或全局限制。异步写入时把 Buffer 按每秒字节数切块，用 Stopwatch 计算理论发送时间，必要时 Delay 后写入底层流。

## IpThrottledStream

用于单 IP 共享带宽。每次从 `SharedData.ClientThrottled` 的 Token 中申请可写字节；没有 Token 时等待 250ms 再尝试，使同一 IP 的并发响应共享额度。

## 响应阶段与清理

中间件调用下游，由下游向包装流写入。`finally` 恢复原始 Body。包装流 Dispose 不关闭底层流，因为底层生命周期属于 ASP.NET Core。

## 实现边界

异步 `WriteAsync` 执行节流，同步 `Write` 当前直接透传。因此自定义中间件和响应代码应优先使用异步写入，否则可能绕开带宽控制。

## 与 SpeedLimit 区别

- Throttled：请求已经被接受，限制响应每秒字节数。
- SpeedLimit：在进入上游前决定请求是否获得配额。

## 排障

- 下载速度未受限：检查匹配 Path、IP 和是否使用异步写入。
- 并发 IP 速度异常：确认真实 IP 和共享 Token 状态。
- 小响应看不出限制：单次 Buffer 很小，测量应使用足够大的文件。

## 公平性与取消

单 IP 多并发下载应共享额度，等待 Token 时必须响应 `RequestAborted`。节流不能长期占用无界任务或计时器；客户端断开后应立即结束等待并释放响应流包装。

## 验证方法

下载足够大的不可压缩文件，测量稳定阶段速度而非首个缓冲。然后从同一 IP 并发下载、从不同 IP 下载并中途取消，检查总速率、独立配额和资源释放。
