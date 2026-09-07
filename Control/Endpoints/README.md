# 控制台 Endpoint 组织约定

控制台后台按业务拆分，一个 `*Endpoints.cs` 只注册一个业务域。`ControlApi` 作为历史兼容入口，只调用各模块的 `Map*Endpoints`，新业务不得继续向该文件追加实现。

每个模块包含：

- 固定业务前缀的 `MapGroup`。
- 本业务请求 DTO、输入校验、状态码和审计动作。
- 对应用服务的调用，不直接实现持久化或安全检测算法。

跨业务基础设施放在 `Control/Infrastructure`。动态配置统一通过 `ControlConfigurationPatchService`，它负责合并补丁、创建配置版本、原子写入、重载验证和失败回滚。插件业务优先实现 `IPluginApiHandler`，通过 `/api/plugins/{pluginId}/proxy/...` 暴露，不再进入控制台核心 API。

当前模块按业务域拆分。ControlApi.cs 只负责中间件顺序和模块注册；共享辅助方法位于 Control/Infrastructure/ControlApi.Helpers.cs，请求模型位于 Control/Models/ControlApiModels.cs。

主要 ControlApi 模块：Frontend、Authentication、ErrorTemplates、Intercepts、Dashboard、Overview、ConfigFiles、ConnectionLimits、AccessControl、BlockedIps、IpLogs、CcProtection、GeoAccess、Throttling、AbTesting、LoadBalancing、Routes、FileServer、SimpleResponses、Plugins、Settings、History、Parameters 和 Pcap。

独立扩展模块：

- `WafRuleEndpoints.cs`：WAF 自定义规则枚举、查询、CRUD 和启停。
- `RulePackageEndpoints.cs`：签名规则包安装、下载、灰度、回滚、解释和反馈。
- `AntiBotEndpoints.cs`：Anti-Bot 与可信 JA4 配置。
- `ResponseWafEndpoints.cs`：响应检测、阻断、脱敏和自定义规则。
- `RequestSecurityEndpoints.cs`：请求规范化限制、协议异常和语义检测。

新增控制台业务时应新建对应的 Endpoint 文件，并只在 ControlApi.cs 中增加一次注册调用，避免把处理逻辑重新写回中心入口。
