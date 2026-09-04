# AcmeChallengeMiddleware：证书验证短路

## 位置

位于全局插件之后、反向代理和 AutoHttps 之前。它必须在 HTTP 到 HTTPS 重定向之前处理。

## 匹配范围

仅匹配：

```text
/.well-known/acme-challenge/{token}
```

其他路径直接调用下游。

## 请求流程

1. 提取 Challenge Token。
2. 调用 `IAcmeService.GetChallengeResponse(token)`。
3. 找到时以 `text/plain` 返回 200 和验证内容。
4. 未找到时返回 404。
5. 两种 Challenge 结果都直接短路，不进入业务上游。

## 为什么不能放在 AutoHttps 后面

HTTP-01 CA 需要通过公网 HTTP 访问 Token。如果先永久重定向，CA、外层代理或尚未生效的 HTTPS 证书可能导致验证失败。

## 配置来源

Token 由 ACME 服务在签发/续期流程中临时维护。中间件不读取站点业务路由，也不应该把 Challenge 转发给应用。

## 排障

- 404：Token 未注册、已过期或请求到达了错误实例。
- 请求未到 LyWaf：检查 DNS、NAT、防火墙和公网 80 端口。
- 多实例随机失败：Challenge 状态需要路由到创建 Token 的实例或共享。
- 被重定向：检查是否有 LyWaf 之前的 CDN/代理强制 HTTPS。

## 安全边界

Token 必须精确匹配并具有短生命周期，响应只返回 ACME 服务登记的纯文本。该路径不能接受任意文件名，也不能映射到本地证书目录。日志可记录命中与否，但不应长期保存完整验证内容。

## 验证方法

签发前临时登记测试 Token，从公网执行 `curl http://domain/.well-known/acme-challenge/token`，确认状态码、Content-Type 和正文。随后删除 Token 并确认返回 404；再检查普通 `/.well-known/` 路径会继续正常路由。
