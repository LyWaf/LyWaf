# Admin API 安全工作流

LyWaf 的管理面与代理流量使用独立控制监听。默认地址为 `127.0.0.1:7030`，非回环地址必须显式设置 `ManagementApi.AllowRemoteControl=true`，否则 Host 在绑定端口前拒绝启动。

## 基础配置

```ly
control_listen = "http://127.0.0.1:7030"

ManagementApi {
    AllowRemoteControl = false
    MaxRequestBodyBytes = 4194304
    ReadRequestsPerMinute = 600
    WriteRequestsPerMinute = 120
    MaxTrackedClients = 10000
    AuditMutatingRequests = true
}
```

确需远程控制时，应同时使用 HTTPS、主机防火墙或私网 ACL，不能把 `AllowRemoteControl` 当作认证。经管理反向代理转发时，默认限流来源是直接连接 IP；应由可信代理先做用户级限流，避免所有管理员共享一个额度或伪造来源头。

## 请求处理顺序

1. 仅控制端口且 `/api` 路径进入管理契约中间件。
2. `/api/v1` 重写为内部规范路径并附加 API 版本、请求 ID。
3. 设置 Kestrel 请求体硬上限；已知 `Content-Length` 超限立即返回 413，分块正文读取超过限制也由服务器拒绝。
4. 按直接来源 IP 和读写方法分别执行一分钟固定窗口限流，超限返回 429 与 `Retry-After`。
5. 匿名端点、机器 HMAC 端点或 JWT/Basic 认证按各自策略校验。
6. 端点执行后，写方法记录规范路径、状态码和请求 ID 的传输层审计。

来源跟踪有最大容量，超过后请求进入共享溢出桶，避免随机来源持续制造字典条目。登录接口另有凭据失败限制；通用 API 限流不能替代登录防爆破。

## 配置写入

完整配置应用沿用第 30、31 章的流程：候选内容先解析和验证，生成不可变版本，使用同目录临时文件原子替换，触发运行时加载和冒烟检查；失败时恢复写入前版本。监听、依赖注入和中间件结构变化仍提示重启，不伪装成完全热更新。

管理 API 不提供“先写坏文件再尝试启动”的路径。自动化客户端应使用 `/api/v1`，保存返回的请求 ID、配置版本 ID 和 SHA-256，发生失败时根据稳定错误信封处理，而不是匹配中文消息。

## 认证与审计

- 控制台用户使用短期 Bearer Token；Basic Auth 用于受控命令行场景。
- 分发与运行状态 Agent 使用独立 HMAC、时间窗和 nonce 防重放，不接受控制台 JWT 替代。
- 写请求统一记录 `Admin API METHOD PATH => STATUS [REQUEST_ID]`，业务端点可另外记录“修改密码、更新证书、回滚配置”等语义审计。
- 审计日志不得记录 Authorization、密码、私钥、完整配置正文或插件敏感参数。

推荐告警包括持续 401、429、413、连续 5xx、非工作窗口的配置写入和远程控制监听启用。请求 ID 可关联运行日志与审计日志。
