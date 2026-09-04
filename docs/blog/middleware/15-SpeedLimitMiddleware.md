# SpeedLimitMiddleware：请求速率限制

## 位置

位于响应带宽节流后、CC 错误响应监控前。它读取已经匹配好的 YARP Route Metadata。

## 策略来源

路由 Metadata 中存在 `RateLimiter` 时，将其值作为策略名称，通过 `ISpeedLimitService.Get(policy)` 获取实际限流器。

## 请求流程

1. 获取当前 Endpoint 和 RouteModel。
2. 路由没有 Metadata 或 RateLimiter：继续。
3. 找不到命名策略：记录警告后继续。
4. 找到策略：异步申请一个 Permit。
5. Lease 成功：继续。
6. Lease 失败：返回配置的 RejectCode 并短路。
7. Lease 使用 `using`，请求离开当前作用域后释放相关资源。

## 为什么绑定路由

不同接口成本差异很大。登录、搜索、导出和静态资源需要不同容量。路由 Metadata 让配置转换阶段把策略与具体 Host/Path 绑定。

## 找不到策略为何继续

当前实现选择可用性优先，记录警告而不阻断。生产环境必须监控该警告，否则拼写错误会让预期限流静默失效。

## 与其他限制配合

- AccessControl ConnectionLimit 控制同时进行的连接。
- CC 识别统计窗口中的异常行为。
- SpeedLimit 控制请求许可速率。
- Throttled 控制响应带宽。

## 排障

- 一直不限流：检查 Route Metadata 是否有正确策略名。
- 所有用户共享一个桶：检查策略分区键设计。
- 返回码不符合预期：检查 SpeedLimit RejectCode。
- 429 后立即恢复或恢复太慢：检查窗口和补充速率。

## 分区键与响应头

策略应明确按全局、客户端 IP、用户、API Key 还是路径分区。依赖真实 IP 时必须先完成可信代理配置。拒绝响应可提供 `Retry-After` 或限流元数据，但不能暴露内部容量细节。

## 验证方法

以固定并发和固定速率跨越窗口，统计成功与拒绝数量。再测试多个分区键、策略不存在、请求取消和配置热更新，确认 Lease 始终释放且警告可被监控发现。
