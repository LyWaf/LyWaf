# 正确配置 CORS：从预检到凭据请求

> 专栏第 9 篇。目标是理解浏览器为何拦截跨域响应，以及 LyWaf 如何处理预检。

## CORS 不是认证

CORS 是浏览器读取响应的许可机制。非浏览器客户端不受它限制，攻击者仍可直接请求 API。因此 Token、Cookie 校验、CSRF 和业务权限不能省略。

## 什么是 Origin

Origin 由 scheme、host 和 port 组成：

```text
https://app.example.com
http://app.example.com
https://app.example.com:8443
```

三者互不相同。允许域名时不要忽略协议和非标准端口。

## 显式策略

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

测试环境可用 `Cors = true` 快速开放，但生产应限制来源、方法和请求头。

## 预检发生了什么

带 `Access-Control-Request-Method` 的 OPTIONS 是浏览器询问“后续请求是否被允许”。LyWaf 匹配路由后直接返回 204，不访问上游。即使业务路由只允许 GET/POST，启用 CORS 后也会让 OPTIONS 命中。

```powershell
curl.exe -i -X OPTIONS http://127.0.0.1/api/users `
  -H "Host: api.example.com" `
  -H "Origin: https://app.example.com" `
  -H "Access-Control-Request-Method: POST" `
  -H "Access-Control-Request-Headers: Authorization, Content-Type"
```

## 凭据请求

Cookie 跨域需要客户端启用 credentials，服务端 `AllowCredentials = true`，Cookie 本身还要满足 SameSite 和 Secure。允许凭据时不能返回通配 Origin；LyWaf 会回显已验证的请求 Origin。

## 路径覆盖

```ly
api.example.com {
    Cors = true

    /admin/* {
        Cors = false
        proxy = ["http://127.0.0.1:8080"]
    }
}
```

站点策略可被路径块覆盖。管理 API 通常不应因为公开 API 需要跨域而一起开放。

## 排错顺序

1. 检查浏览器实际 Origin。
2. 单独测试 OPTIONS 的状态码和允许头。
3. 再测试带 Origin 的普通请求。
4. 401/403 通常来自认证或 WAF，不是 CORS。
5. 自定义响应头需加入 `ExposedHeaders` 才能被 JavaScript 读取。

## 浏览器实验

准备两个不同 Origin 的测试页面，分别发简单请求、带 JSON 的预检请求和凭据请求。检查浏览器网络面板中的预检与业务请求，并验证拒绝来源虽然可能收到 HTTP 响应，但 JavaScript 无法读取。

## 完成标准

来源、方法和请求头采用最小授权；凭据模式不使用通配 Origin；动态来源响应包含 `Vary: Origin`；自定义响应头只有加入 ExposedHeaders 后才能读取。

## 本篇小结

到这里请求侧控制已经比较完整。下一篇转向响应完整性：上游页面被篡改后，如何继续向用户返回已确认内容。
