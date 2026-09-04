# CorsMiddleware：Origin 与预检

## 位置

位于带宽统计之后、响应压缩和安全链之前。它依赖 YARP Endpoint 已匹配完成，并从 RouteModel.Metadata 读取 `_Cors`。

## 配置如何到达中间件

`config.ly` 中的 `Cors` 块由转换器标准化并序列化进路由 Metadata。中间件每次按当前路由获取允许来源、方法、请求头、暴露头、凭据和 MaxAge。

## 普通请求流程

1. 路由无 `_Cors`：继续。
2. 请求无 Origin：继续。
3. Origin 不在允许列表：继续，但不授予跨域响应头。
4. Origin 合法：注册 `Response.OnStarting`。
5. 响应发送前写入 Allow-Origin、Vary、Methods、Headers 等。

使用 OnStarting 能减少同名上游响应头覆盖最终策略的风险。

## 预检短路

请求同时满足 OPTIONS 和 `Access-Control-Request-Method` 时视为预检。合法 Origin 直接返回 204，不访问 WAF 后续链和上游。

## 通配与凭据

无凭据且允许 `*` 时返回通配 Origin；启用凭据时必须回显已验证的具体 Origin。`https://*.example.com` 会匹配子域，但 scheme 和 port 仍需一致。

## 顺序原因

预检是一种浏览器权限询问，不应要求业务上游处理。它放在压缩之前，204 空响应也不会进入不必要的压缩缓冲。

## 排障

- 浏览器失败但 curl 正常：curl 不执行浏览器 CORS 规则。
- 预检 204、业务 401：CORS 已通过，问题在认证或安全规则。
- Cookie 不发送：同时检查客户端 credentials、AllowCredentials、SameSite 和 Secure。
- Header 读不到：响应头需加入 ExposedHeaders。

## 缓存安全

动态回显 Origin 时必须设置 `Vary: Origin`，否则 CDN 可能把一个来源的授权响应复用于另一个来源。预检缓存时间不宜在策略频繁变化时设置过长；策略收紧后还要考虑浏览器已有缓存。

## 验证命令

```powershell
curl.exe -i -X OPTIONS https://api.example.com/data `
  -H "Origin: https://app.example.com" `
  -H "Access-Control-Request-Method: POST" `
  -H "Access-Control-Request-Headers: Content-Type"
```

再分别使用允许和拒绝 Origin 发普通请求，检查 Allow-Origin、Vary、凭据和暴露头。服务端返回 200 不代表浏览器一定允许脚本读取响应。
