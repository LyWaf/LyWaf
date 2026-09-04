# AutoHttpsMiddleware：HTTPS 重定向

## 位置

位于 IpLog 之后、WAF 之前。ACME Challenge 已经在更外层处理，因此不会被这里重定向。

## 配置快照

构造中间件时，它从 WafInfo 的监听列表建立：

```text
HTTP 本地端口 -> AutoHttpsPort
```

只有合法的 1 至 65535 HTTPS 端口才进入字典。

## 请求流程

1. 读取 `context.Connection.LocalPort`。
2. 查找对应 HTTPS 端口。
3. 使用请求 Host、Path 和 Query 构造 HTTPS URL。
4. 目标端口为 443 时省略端口，否则显式保留。
5. 调用 `Response.Redirect(..., permanent: true)` 并短路。

没有映射时继续 WAF 和后续代理。

## 为什么在 WAF 之前

HTTP 请求最终只会重定向，不需要先读取 Body 和执行完整攻击检测。提前返回降低开销，也避免同一次客户端访问产生重复 HTTPS/WAF 处理。

## 永久重定向注意事项

永久重定向可能被浏览器和 CDN 缓存。调试配置错误后，即使服务已修改，客户端仍可能使用旧目标。测试阶段可用无缓存客户端或清理缓存。

## 外层 TLS 终止

若 CDN 在外层终止 HTTPS，但回源到 LyWaf 使用 HTTP，LyWaf 看到的本地端口可能触发再次重定向。应正确配置外层协议语义和可信 Forwarded Header，防止循环。

## 排障

- 未重定向：监听配置没有 AutoHttpsPort，或请求进入了不同端口。
- 循环：检查 CDN/LB 到 LyWaf 的协议和 Forwarded 信息。
- 跳错端口：核对 HTTP 与 HTTPS 监听配对。
- ACME 失败：检查是否有 LyWaf 之前的组件强制重定向 Challenge。

## Host 安全

重定向目标使用请求 Host 时，必须确保请求已通过合法站点路由，避免不受信任 Host 形成开放重定向。非标准 HTTPS 端口需要显式保留，IPv6 Host 构造也要保持方括号格式。

## 验证方法

对每组 HTTP 监听执行 `curl -I`，检查 Location 的 scheme、host、port、path 和 query。再请求 ACME Challenge、未知 Host 和外层 TLS 回源场景，确认没有循环或跨域重定向。
