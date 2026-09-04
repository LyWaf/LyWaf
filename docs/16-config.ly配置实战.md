# config.ly 配置实战

## 配置模型

`config.ly` 会被解析为内部配置，再生成 YARP 路由和各服务 Options。站点块决定 Host，路径块决定 Path，块内的 `proxy`、`file_server` 或 `respond` 决定处理方式。

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

## 监听与控制台

```ly
http_port = 80
https_port = 443
control_listen = "127.0.0.1:7030"
```

控制台不要直接绑定 `0.0.0.0` 暴露公网。如需远程管理，优先使用 VPN、SSH 隧道或仅允许管理网段的外层代理。

## 多站点和多路径

```ly
api.example.com {
    /v1/* {
        method = ["GET", "POST"]
        proxy = ["http://127.0.0.1:8101"]
    }

    /static/* {
        file_server {
            root = "./wwwroot"
            browse = false
            index = ["index.html"]
            max_file_size = 104857600
            precompressed = true
            follow_links = false
            show_hidden = false
            cache_control = "public, max-age=3600"
        }
    }

    proxy = ["http://127.0.0.1:8100"]
}
```

更具体的 Host 和 Path 优先。一个站点的根路径只能选择一个默认处理器，避免同时配置根级 `proxy`、`file_server` 和 `respond`。

## 多上游和负载均衡

```ly
example.com {
    proxy = ["http://10.0.0.11:8080", "http://10.0.0.12:8080"]
    lb_policy = RoundRobin
    health_check {
        Active {
            Enabled = true
            Interval = "00:00:10"
            Timeout = "00:00:03"
            Path = "/health"
        }
    }
}
```

健康检查接口应轻量、无认证，并真实反映应用是否能处理请求。负载策略详见 [负载均衡](07-负载均衡.md)。

## 上游重试

```ly
example.com {
    proxy = ["http://10.0.0.11:8080", "http://10.0.0.12:8080"]
    retry {
        Enabled = true
        MaxAttempts = 3
        Methods = ["GET", "HEAD", "OPTIONS"]
        StatusCodes = [502, 503, 504]
        RetryNetworkErrors = true
        PerTryTimeoutMs = 2000
        TotalTimeoutMs = 6000
        BaseBackoffMs = 100
        MaxBackoffMs = 1000
        BudgetPercent = 20
        BudgetMinRetriesPerSecond = 1
    }
}
```

重试默认关闭；启用后仍默认只处理安全、幂等且无请求体的方法。超时、Retry Budget 和指标说明见 [上游重试策略](26-上游重试策略.md)。

## Cluster 容量保护

```ly
example.com {
    proxy {
        to = ["http://10.0.0.11:8080", "http://10.0.0.12:8080"]
        circuit_breaker {
            Enabled = true
            MaxConnections = 200
            MaxActiveRequests = 1000
            MaxQueuedRequests = 500
            MaxConcurrentRetries = 100
        }
    }
}
```

队列或重试容量耗尽时会快速返回 `503`。参数含义与监控指标见 [Cluster 熔断与容量保护](27-Cluster熔断与容量保护.md)。

## 异常实例驱逐

```ly
example.com {
    proxy {
        to = ["http://10.0.0.11:8080", "http://10.0.0.12:8080"]
        outlier_detection {
            Enabled = true
            ConsecutiveFailures = 5
            Consecutive5xx = 5
            WindowSize = 100
            MinimumVolume = 20
            ErrorRatePercent = 50
            EjectionDurationMs = 30000
            MaxEjectionPercent = 50
        }
    }
}
```

该功能使用 YARP Passive 健康状态，不覆盖主动健康检查。判定和恢复机制见 [异常实例自动驱逐](28-异常实例自动驱逐.md)。

## 请求头和响应头

```ly
example.com {
    HeaderUps {
        X-Real-IP = "{ClientIp}"
        X-Forwarded-Host = "{Host}"
    }
    HeaderDowns {
        X-Content-Type-Options = "nosniff"
        Referrer-Policy = "same-origin"
    }
    proxy = ["http://127.0.0.1:8080"]
}
```

`HeaderUps` 发送给上游，`HeaderDowns` 返回给客户端。CORS 不应仅靠手写 HeaderDowns，预检应使用专用 `Cors` 配置。

## 变量、片段和导入

- `var name = "value"` 定义变量，`${name}` 引用。
- `(name) { ... }` 定义可复用片段，`import name` 使用。
- `import "sites/*.ly"` 拆分大配置。
- 环境相关的地址和密钥优先从环境变量注入，不要提交到仓库。

## 修改配置后的验证

```powershell
dotnet LyWaf.dll run -c config.ly
dotnet LyWaf.dll reload -c config.ly
```

当前 CLI 的 `validate` 主要面向内部 YAML 配置；对 `.ly` 配置，最可靠的校验是通过配置加载器启动或重载，并检查解析日志。生产变更前应在测试实例加载同一份配置。

## 常见错误

| 现象 | 原因 | 处理 |
|---|---|---|
| 域名请求进入错误上游 | Host 或端口未匹配 | 检查站点地址和请求 Host |
| HTTPS 启动失败 | 证书路径、私钥或 SNI 不匹配 | 检查文件权限和 `Certs` 键名 |
| 路径规则不生效 | `*` 与 catch-all 语义错误 | 使用示例中的 `/api/*` 形式 |
| 修改后未生效 | 变更涉及监听端口或启动期服务 | 执行完整重启 |
| 上游获得错误客户端 IP | 未配置转发头或外层代理未受信任 | 明确真实 IP 信任链 |

## 配置评审清单

- 公网 Host 是否唯一命中预期监听和路由。
- 控制端是否与业务端口隔离。
- 每条路由是否只有一个处理器。
- 上游 scheme、端口和健康路径是否正确。
- 通配路径是否过宽并具有必要排除。
- 证书、日志和数据是否使用可靠路径。
- 插件和安全功能是否配置容量、超时和开关。

## 变更与自动化

应用前保存配置版本；重载失败时恢复主配置后重新校验。监听、证书绑定、DI 和插件管线变化安排重启。发布流水线至少执行语法校验、敏感值扫描和路由冒烟测试。
