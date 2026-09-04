# LoadBalancing：YARP 上游选择阶段

## 它是什么

`proxyApp.UseLoadBalancing()` 是 YARP 代理管线阶段，不是仓库自定义 Middleware 类。因为 LyWaf 自定义了 `MapReverseProxy` 管线，所以必须显式加入。

## 位置

位于 SimpleRes 和 FileProvider 后。只有未被本地处理器短路的代理路由才需要选择上游 Destination。

## 输入

- 当前 RouteModel 的 ClusterId。
- Cluster 的 Destinations。
- 健康检查结果。
- LoadBalancingPolicy。
- 自定义策略需要的 Metadata，如权重、HashKey 或 A/B 信息。

## 处理流程

1. 根据 Cluster 获取候选 Destination。
2. 排除健康状态不可用的节点。
3. 使用 RoundRobin、LeastRequests、加权、Hash 等策略选择节点。
4. 将选定目标交给后续 YARP 转发阶段。

## 不负责什么

负载均衡不执行业务重试语义，不保证会话状态，也不能让全部故障节点恢复。健康检查、超时、应用幂等和会话外置仍需单独设计。

## 为什么在本地处理器之后

SimpleRes 和 FileProvider 不需要后端。提前选择上游会产生无意义的 Cluster 依赖，也可能让本地路由在无健康节点时错误失败。

## 排障

- 路由正确但没有目标：检查 ClusterId 和 Destinations。
- 始终进入同一节点：检查策略、权重和健康状态。
- 节点恢复后不接流量：检查主动/被动健康策略。
- 自定义管线忘记 UseLoadBalancing：路由存在也无法正常选择上游。

## 故障语义

目标选择成功不代表请求成功。连接失败、超时和 5xx 是否重试必须结合方法幂等性决定，不能对带副作用的 POST 无条件重放。会话状态应外置或采用明确亲和策略。

## 验证方法

给各上游返回可识别实例 ID，连续请求观察分布。停止、恢复和慢化单个节点，检查健康摘除、恢复、最少连接和无可用节点行为。自定义策略还需验证权重为零、缺失元数据和扩缩容。
