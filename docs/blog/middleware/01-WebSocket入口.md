# WebSocket 入口阶段解析

## 它是不是自定义中间件

这里使用 ASP.NET Core 的 `UseWebSockets()`，不是仓库中的自定义 Middleware 类。但后续插件和 YARP 依赖它提供的标准 WebSocket 检测能力，因此属于完整管线的入口阶段。

## 位置

它位于控制台/插件 API 映射之后、全局插件之前。此后 `context.WebSockets.IsWebSocketRequest` 才能可靠识别升级请求。

## 请求流程

普通 HTTP 请求不会被短路，继续下游。包含合法 Upgrade 请求时，ASP.NET Core 准备 WebSocket 能力，实际连接仍由后续 YARP 或业务中间件接受。

WebSocket 的 `await next()` 生命周期与普通 HTTP 不同：代理建立长连接后，调用通常直到连接关闭才返回。因此插件请求耗时可能等于整个 WebSocket 会话时间。

## 为什么放在前面

`BasePluginMiddleware` 需要区分 HTTP 和 WebSocket 活跃连接。如果先运行插件再启用 WebSocket，只能依赖原始 Header 兜底，状态识别不够统一。

## 风险与排障

- 普通 HTTP 正常但 WebSocket 失败：检查外层 CDN、Upgrade/Connection Header 和上游协议。
- 连接建立后立即关闭：检查上游超时和应用日志。
- 活跃连接长期不下降：确认客户端是否正常关闭，并检查 YARP 长连接生命周期。

## 结论

该阶段不负责代理数据，但为后续组件提供正确的协议语义。它的价值是“让管线知道这是一条长连接”。

## 验证方法

使用普通 HTTP 和 WebSocket 客户端访问同一路由。普通请求应快速结束；WebSocket 应完成 `101 Switching Protocols`，双向消息正常，连接关闭后活跃连接数下降。测试时同时经过外层 CDN，确认它没有移除 `Upgrade` 和 `Connection`。

## 实现注意

不要为 WebSocket 请求缓冲完整响应体、执行普通响应压缩或设置很短的 HTTP 请求超时。统计耗时应明确标注为会话持续时间。若插件订阅请求完成事件，应避免在长连接关闭时集中执行昂贵同步操作。
