# CcResponseMonitorMiddleware：错误响应反馈

## 位置

位于请求速率限制之后、普通插件和本地响应处理之前。它调用下游后再执行，因此能看到插件、SimpleRes、FileProvider 或 YARP 的最终状态码。

## 响应流程

```csharp
await next(context);
if (context.Response.StatusCode >= 400)
    RecordErrorEvent(clientIp, statusCode);
```

它没有请求前检查，也不改变本次响应。唯一职责是把错误状态反馈给 CC 统计。

## 为何必须等待下游

FrequentError 规则依赖实际 4xx/5xx。状态码在文件查找、上游调用或插件处理前尚未确定，把监控放在下游之前只能看到默认 200。

## 与 CcProtection 的闭环

当前请求的错误由 CcResponseMonitor 记录；后续请求进入 CcProtection 时，规则读取时间窗口中的错误次数并决定是否处置。

这是跨请求反馈，不会因为当前响应变成 500 就立即在同一次请求前半段触发 CC。

## 覆盖边界

它只记录已经进入它下游的响应。更前面的 AccessControl、WAF、Captcha、CC 或 SpeedLimit 短路不会经过其 `await next`，因此不会被作为“上游错误”重复统计。

## 排障

- FrequentError 不触发：确认错误发生在该中间件下游并核对时间窗口。
- 业务 4xx 导致误判：按 Path/StatusCode 收窄规则。
- 需要记录 3xx：当前只处理大于等于 400。

## 数据基数与清理

错误事件通常按 IP、路径和状态码聚合，必须设置时间窗口与过期清理。原始 Query 不应直接作为键，否则随机参数会制造高基数内存压力；可使用标准化路由或受限路径。

## 验证方法

让上游依次返回 200、404、429 和 500，确认只有目标错误进入窗口。随后等待窗口过期，验证 FrequentError 不再触发；对前置 WAF 短路请求确认不会被重复计为上游错误。
