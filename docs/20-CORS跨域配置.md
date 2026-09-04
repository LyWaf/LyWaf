# CORS 跨域配置

## 适用范围

CORS 决定浏览器页面能否跨 Origin 读取响应。它不验证调用者身份，也不能替代 Token、Cookie 校验、CSRF 防护或 WAF。

## 最小配置

对站点全部路由开放：

```ly
api.example.com {
    Cors = true
    proxy = ["http://127.0.0.1:8080"]
}
```

`true` 使用默认策略：任意来源、常见 HTTP 方法、任意请求头，预检缓存 600 秒。生产 API 更推荐显式限制。

## 完整配置

```ly
api.example.com {
    Cors {
        AllowedOrigins = ["https://app.example.com", "https://*.example.com"]
        AllowedMethods = ["GET", "POST", "PUT", "DELETE"]
        AllowedHeaders = ["Content-Type", "Authorization", "X-Request-Id"]
        ExposedHeaders = ["X-Request-Id"]
        AllowCredentials = true
        MaxAge = 600
    }
    proxy = ["http://127.0.0.1:8080"]
}
```

| 配置项 | 含义 | 默认值 |
|---|---|---|
| `AllowedOrigins` | 允许的完整 Origin，支持 `https://*.example.com` | `["*"]` |
| `AllowedMethods` | 浏览器可发起的方法 | 常见方法 |
| `AllowedHeaders` | 跨域请求可携带的请求头 | `["*"]` |
| `ExposedHeaders` | JavaScript 可读取的响应头 | 空 |
| `AllowCredentials` | 允许 Cookie/认证信息 | `false` |
| `MaxAge` | 预检缓存秒数 | `600` |

启用凭据且来源为 `*` 时，LyWaf 会回显已验证的请求 Origin，而不是返回不合法的 `Access-Control-Allow-Origin: *`。

## 路径覆盖

```ly
api.example.com {
    Cors = true

    /admin/* {
        Cors = false
        proxy = ["http://127.0.0.1:8080"]
    }

    /partner/* {
        Cors {
            AllowedOrigins = ["https://partner.example.net"]
            AllowedMethods = ["GET", "POST"]
        }
        proxy = ["http://127.0.0.1:8080"]
    }
}
```

路径块继承站点策略，也可以覆盖或通过 `Cors = false` 关闭。

## 预检行为

带 `Access-Control-Request-Method` 的 `OPTIONS` 请求由 LyWaf 直接返回 `204`，不会访问上游。即使业务路由限定为 GET/POST，配置 CORS 后也会让 OPTIONS 命中该路由。

## 测试

```powershell
curl.exe -i -X OPTIONS http://127.0.0.1/api/users `
  -H "Host: api.example.com" `
  -H "Origin: https://app.example.com" `
  -H "Access-Control-Request-Method: POST" `
  -H "Access-Control-Request-Headers: Authorization, Content-Type"
```

普通请求也要带 Origin 验证：

```powershell
curl.exe -i http://127.0.0.1/api/users -H "Host: api.example.com" -H "Origin: https://app.example.com"
```

## 常见问题

- 浏览器报 CORS，但 curl 正常：curl 不执行浏览器 CORS 检查，应检查响应头和预检。
- Cookie 未发送：客户端需启用 credentials，服务端需 `AllowCredentials = true`，Cookie 的 SameSite/Secure 也必须匹配。
- 自定义头读不到：把它加入 `ExposedHeaders`。
- 预检成功但业务请求 401/403：CORS 已通过，失败来自认证或 WAF。
- CDN 缓存串 Origin：响应包含 `Vary: Origin`，CDN 也必须尊重该头。

## 安全评审

来源写完整 Origin，包括 scheme 和非标准端口。凭据模式禁止 `*`，并检查 Cookie 的 SameSite、Secure 和 Domain。允许方法与请求头采用最小集合。

## 缓存与验收

`MaxAge` 会缓存预检结果，收紧策略后需清理浏览器和 CDN 缓存。用浏览器与 curl 测试允许、拒绝、无 Origin、简单请求、预检、凭据和暴露头。预检成功但业务 401/403 时转查认证、CSRF、WAF 和上游。
