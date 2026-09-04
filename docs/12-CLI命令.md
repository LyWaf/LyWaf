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

## 推荐工作流

首次运行先校验配置，再以前台模式观察日志：

```powershell
./lywaf validate -c config.ly
./lywaf run -c config.ly
```

确认监听、路由和上游正常后，再交给 systemd、Windows Service 包装器或其他进程管理器。生产环境不要同时使用 CLI 后台模式和操作系统服务重复拉起同一实例。

## 路径与环境变量

服务模式下工作目录可能不同，配置、证书、数据库和日志建议使用绝对路径。`--env` 文件应限制读取权限，不提交到仓库；命令行 `-e` 可能出现在进程列表中，不适合传递长期密钥。

## 排障

命令无响应时先确认是否为前台阻塞模式。启动失败依次检查配置语法、端口占用、证书权限、数据目录权限和 .NET Runtime。停止或重载应使用与启动相同的 PID 文件和工作目录。

执行破坏性重置命令前备份认证和配置文件。自动化脚本应检查退出码，不要只匹配控制台文字。
