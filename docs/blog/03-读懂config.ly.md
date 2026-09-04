# 读懂 config.ly：域名、路径与配置复用

> 专栏第 3 篇。目标是理解配置如何变成路由，而不是机械复制一份大配置。

## 三层结构

可以把 `config.ly` 理解成三层：

1. 全局层：监听端口、证书、统计、安全和插件。
2. 站点层：域名或 `:端口`。
3. 路径层：站点下的 `/api/*`、`/static/*` 等规则。

```ly
api.example.com {
    /v1/* {
        proxy = ["http://127.0.0.1:8101"]
    }
    proxy = ["http://127.0.0.1:8100"]
}
```

`/v1/*` 进入 8101，其余路径进入 8100。更具体路径优先于默认路由。

## Host 与端口

以下三种站点含义不同：

```ly
example.com { ... }
localhost:8088 { ... }
:9090 { ... }
```

第一种按域名匹配，第二种同时包含非标准端口，第三种只按监听端口提供无域名限制的站点。

## 每条路由只能有一个处理器

一个默认路由不能同时是反向代理、文件服务和固定响应：

```ly
example.com {
    proxy = ["http://127.0.0.1:8080"]
    # 这里不能再放根级 file_server 或 respond
}
```

需要混合时拆成路径块。

## 静态文件与固定响应

```ly
example.com {
    /assets/* {
        file_server {
            root = "./wwwroot"
            browse = false
            index = ["index.html"]
        }
    }

    /health {
        respond {
            status = 200
            body = "ok"
        }
    }

    proxy = ["http://127.0.0.1:8080"]
}
```

生产环境通常关闭目录浏览，健康检查响应保持轻量。

## 变量和公共片段

```ly
var backend = "127.0.0.1:8080"

(proxy_headers) {
    HeaderUps {
        X-Real-IP = "{ClientIp}"
        X-Forwarded-Proto = "{Scheme}"
    }
}

example.com {
    import proxy_headers
    proxy = ["http://${backend}"]
}
```

变量处理环境差异，片段消除重复。大型配置还可以 `import "sites/*.ly"` 按站点拆分。

## 请求头与响应头

`HeaderUps` 发给上游，`HeaderDowns` 发给客户端：

```ly
HeaderDowns {
    X-Content-Type-Options = "nosniff"
    Referrer-Policy = "same-origin"
}
```

CORS 不建议靠 HeaderDowns 拼装，因为浏览器预检还涉及 OPTIONS 路由。专栏第 8 篇会使用专用 Cors 配置。

## 修改后的验证原则

每次只改一类能力，先在测试实例加载，再检查日志和实际请求。监听端口、Kestrel 或启动期插件设置发生变化时，应完整重启。

## 读者实验

在测试配置中增加一个精确 API 路径、一个静态文件路径和一个固定响应路径。分别请求命中与未命中地址，记录 RouteId 和上游是否收到请求。再修改一个需要重启的监听项，观察它与普通路由重载的差异。

## 配置审查

提交配置前检查 Host/端口唯一性、处理器冲突、通配范围、变量空值、绝对路径和敏感字段。配置解析成功只证明语法可读，不能证明请求语义正确。

## 本篇小结

掌握 Host、Path 和处理器后，复杂配置只是组合问题。下一篇加入 HTTPS，并分析手工证书和 ACME 自动证书的取舍。
