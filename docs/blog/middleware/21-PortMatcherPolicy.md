# PortMatcherPolicy：进入中间件前的路由筛选

## 为什么单独讲

它不是 Middleware，而是 ASP.NET Core `MatcherPolicy`。请求只有先选中 Endpoint，才会进入代理中间件。很多“中间件没有执行”的问题实际发生在此阶段。

## 解决的问题

同一应用监听多个端口，多个 YARP 路由可能拥有相同 Path 和相近 Host。默认匹配可能产生 AmbiguousMatchException，或让带端口 Host 进入错误路由。

## 匹配内容

- 没有 Hosts 的路由作为通配候选。
- `host:port` 同时检查 Host 和实际请求端口。
- IPv6 `[address]:port` 正确拆分。
- `*`、`0.0.0.0` 和 `*.example.com` 通配。
- localhost 与本地地址语义兼容。
- 同时存在具体和通配候选时优先具体匹配。

## 执行位置

Endpoint Routing 阶段早于 `MapReverseProxy` 分支。因此 CORS、AccessControl 和 WAF 读取到的 RouteModel 已经是 PortMatcherPolicy 筛选后的结果。

## 对 CORS 的影响

CorsMiddleware 依赖当前 Route Metadata。如果 Host/Port 没有选中预期路由，即使 Cors 配置正确，中间件也读不到对应 `_Cors`。

## 排障方法

1. 用 curl 显式设置 Host。
2. 核对请求实际 LocalPort。
3. 检查站点是否写成 `domain:port`。
4. 检查更具体 Host 是否覆盖通配路由。
5. 请求完全没有代理日志时先查 Endpoint 匹配。

## 结论

PortMatcherPolicy 是中间件管线的“入口选择器”。它不处理响应，却决定后续所有路由级配置、插件和安全策略是否有机会执行。

## 规范化细节

Host 比较应忽略域名大小写，但端口必须精确。IPv6 字面量、默认端口、通配子域和无 Host 路由需要分开测试。更具体候选优先时仍要保证结果唯一，否则应在配置阶段拒绝歧义。

## 验证矩阵

对同一路径组合不同本地端口、Host Header、`host:port`、通配域名、localhost、IPv4 和 IPv6。确认每次选中的 RouteId 与预期一致；构造两个同等具体度路由，检查系统能明确报告歧义。
