# 插件代理管道阶段

流量分析插件通过 `IPluginProxyStages` 注册 CC 请求检测及错误响应统计，主程序只保留阶段入口，不直接装配 CC 中间件。

- `RequestProtection`：位于验证码之后、限流之前。
- `ResponseMonitoring`：位于限流之后，统计下游错误响应。

代理管道为启动时已发现的插件预先注册，每个请求根据插件是否处于 `Running` 状态决定是否执行。停止插件后新请求跳过插件，已进入管道的请求允许完成；重新启用不需要重建管道。既有封禁不会因停止插件自动解除。

所有已发现插件均注册依赖服务，插件的服务注册方法不能启动后台工作；后台工作应由生命周期启动和停止方法管理。管道配置方法只负责装配，不应根据当时的启用状态省略中间件，应在请求执行时读取动态业务选项。

这个机制不支持在启动后安装全新插件并向已有服务容器添加依赖，此类变更仍需重启。

CC 菜单、页面、封禁日志接口、规则管理 API 和代理阶段均由流量分析插件提供。共享统计持久化服务、封禁状态、审计和权限仍由宿主提供。

## CC 管理 API

统一前缀：`/api/v1/plugins/analysis/proxy/`。旧 `/api/cc/*` 和 `/api/statistic/*` 管理路由已移除，调用方需要切换。

| 方法 | 插件相对路径 | 用途 |
| --- | --- | --- |
| GET | `statistic/config` | 白名单路径和路径统计配置 |
| POST | `statistic/white-paths/add`、`statistic/white-paths/remove` | 管理白名单路径 |
| POST | `statistic/path-stas/add`、`statistic/path-stas/remove` | 管理路径统计 |
| GET | `cc/rules` | 基础规则分页查询 |
| POST | `cc/rules/add`、`cc/rules/remove` | 管理基础规则 |
| GET | `cc/advanced`、`cc/advanced/type/{type}` | 高级规则分页查询 |
| GET | `cc/advanced/{ruleId}`、`cc/advanced/enums` | 规则详情和枚举 |
| POST | `cc/advanced/add`、`cc/advanced/update`、`cc/advanced/remove`、`cc/advanced/toggle` | 管理高级规则 |
| GET | `ban-logs` | 最近 1000 条 CC 封禁日志 |

请求模型位于 `Plugins/Sys/Analysis/AnalysisApiModels.cs`。插件读取 JSON 后复用宿主的有界递归字段校验器，保留条件组、枚举、阈值和时长验证。基础规则的 `fbTime` 使用 TimeSpan 字符串，例如 `00:05:00`；前端负责将秒数转换为该格式。

代理仍由宿主校验管理端身份和权限、插件状态与 ConsoleApi 授权，并执行准入限制和审计。禁用插件后代理返回 503，不能通过旧路由绕过。列表保留 `page`、`pageSize`、`search` 等分页参数；参数和业务错误使用统一错误响应。
