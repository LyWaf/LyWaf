# Hedging 并行请求评估

Hedging 在一次上游尝试超过短延迟阈值后，向另一个健康实例并行发送相同请求，以第一个有效响应为准并取消其余尝试。它用于降低少量长尾延迟，不是普通失败重试，也不能替代超时、健康检查和容量保护。

## 当前结论

LyWaf 暂不提供可配置的 Hedging 开关。原因不是并发调用难以实现，而是当前执行位置无法保证“另一个实例”：YARP 先通过 `ILoadBalancingPolicy` 选择 `Destination`，随后才进入 `RetryForwarderHttpClientFactory` 创建的 Cluster `HttpMessageHandler`。该 Handler 收到的是已经确定 authority 的 `HttpRequestMessage`，再次调用 `base.SendAsync` 仍会访问同一地址。

在现有 Retry Handler 内直接并发会产生一个名称像 Hedging、实际却重复轰击同一实例的错误实现，并且不能正确更新另一个 Destination 的并发数、主动/被动健康和异常驱逐状态。因此本阶段保留串行 Retry，不增加误导性配置。

## 正确实现所需边界

未来实现必须位于 Destination 选择之前或成为独立转发调度层，并满足以下契约：

1. 从同一 Cluster 的可用集合中原子选择两个不同 Destination，服从 Priority、Zone、Subset、主动健康、被动健康和 Outlier Detection。
2. 仅允许 `GET`、`HEAD`、`OPTIONS` 等明确可重放方法；带正文请求必须由可重复读取且受大小限制的正文快照支持，默认禁止。
3. 首次尝试达到 `HedgeDelay` 后才能启动第二次尝试，不能在请求开始时无条件翻倍流量。
4. 第一个符合配置状态范围的响应胜出；取消和完整释放失败/落后响应，不能泄漏连接或正文流。
5. 每个额外尝试同时占用 Retry Budget 与 Cluster 的 `MaxConcurrentRetries`，容量不足时不启动 Hedging。
6. 客户端取消和总超时必须终止所有尝试；单次取消不能误标另一个 Destination 为失败。
7. 指标至少区分 `started`、`won_primary`、`won_hedge`、`cancelled`、`budget_rejected` 和 `exhausted`。

## 启用门槛

只有在生产直方图证明少量请求存在明显长尾、Cluster 至少有两个健康实例、上游有富余容量，并完成相同请求并发执行的业务审计后，Hedging 才值得启用。持续高延迟、容量不足或单实例场景应先修复上游，不应靠并发放大请求掩盖问题。

重新评估时需要先完成高级负载均衡中的统一选择接口，让 Retry、Hedging、Priority/Failover 和异常驱逐共享同一个候选集与容量计数。该接口完成前，LyWaf 对外只承诺串行、受预算约束的幂等重试。
