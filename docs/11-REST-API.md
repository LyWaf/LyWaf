# REST API 端点

所有端点定义在 `ControlApi.cs`，除 `/api/auth/login` 外均需 JWT 或 Basic Auth。

## 认证

| 方法 | 端点 | 说明 |
|------|------|------|
| POST | /api/auth/login | 登录获取 JWT token |
| GET | /api/auth/check | 校验当前 token |
| GET | /api/auth/time | 获取服务器时间 |

## 配置管理

| 方法 | 端点 | 说明 |
|------|------|------|
| POST | /api/config/reload | 从文件重载配置 |
| GET | /api/config/download | 下载当前配置文件 |
| POST | /api/config/upload | 上传并应用新配置 |
| POST | /api/config/validate | 校验配置语法 |

## 业务管理

| 方法 | 端点 | 说明 |
|------|------|------|
| GET/POST | /api/routes/* | 路由 CRUD |
| GET/POST | /api/clusters/* | 集群配置 |
| GET/POST | /api/rules/* | WAF 规则管理 |
| GET | /api/statistics/query | 查询 DuckDB 统计 |
| GET | /api/intercept-events | 查看拦截事件 |
| GET/POST | /api/plugins/* | 插件管理与 Action |
| GET/POST | /api/speed-limit/* | 限速配置 |
| GET/POST | /api/access-control/* | IP 控制设置 |
| GET | /api/audit-logs | 查看审计日志 |
| GET | /api/health | 健康检查 |
| POST | /api/stop | 优雅关闭服务 |
