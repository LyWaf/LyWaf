# CLI 命令

| 命令 | 说明 |
|------|------|
| `lywaf run -c config.ly` | 前台运行（阻塞模式） |
| `lywaf start -c config.ly` | 后台服务启动 |
| `lywaf stop` | 停止运行中的服务 |
| `lywaf reload -c config.ly` | 重载配置 |
| `lywaf validate -c config.ly` | 校验配置语法 |
| `lywaf file -l 8080 -r /path` | 快速静态文件服务 |
| `lywaf proxy --from :80 --to upstream` | 快速反向代理 |
| `lywaf respond --listen :8080` | Mock 响应服务 |
| `lywaf environ` | 列出环境变量 |
| `lywaf reset password` | 重置控制台密码 |

## 常用选项

- `--env file.env` — 从文件加载环境变量
- `-e KEY=VALUE` — 内联设置环境变量
- `--pid file.pid` — PID 文件路径
- `--cert-pem, --cert-key` — TLS 证书路径
- `--access-log, --error-log, --perf-log` — 日志文件路径
