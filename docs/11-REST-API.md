# 管理 API 契约

LyWaf 管理 API 只监听控制端口。核心控制端点定义在 `Control/ControlApi.cs`；插件业务端点由 `IPluginApiHandler` 处理，只能经统一插件代理入口访问。

## 版本策略

当前稳定版本为 v1，客户端必须使用 `/api/v1/*`。历史 `/api/*` 路径暂时保持等价，用于旧控制台迁移。旧路径响应包含：

- `Deprecation: true`
- `Sunset: Thu, 03 Sep 2027 00:00:00 GMT`
- `Link: </api/v1>; rel="successor-version"`

所有管理响应均带 `X-LyWaf-Api-Version` 和 `X-Request-Id`。破坏性字段、语义或状态码变化必须新增主版本；同一主版本只允许增加可选字段和端点。废弃接口至少保留 12 个月，先发布替代入口和 `Sunset`，再在下一主版本移除。

证书运行状态使用 `GET /api/v1/settings/certs/status`，返回已加载证书的来源、到期时间、剩余天数、吊销检查状态，以及不含密钥内容的 ACME CA 账户摘要。

## OpenAPI

鉴权后访问 `GET /api/v1/openapi.json` 可获得 OpenAPI 3.1 文档。文档从运行时 `EndpointDataSource` 生成，因此覆盖核心与已注册插件代理路由，不依赖容易过期的静态端点清单。

当前文档稳定描述路径、HTTP 方法、认证和公共错误模型。部分历史端点仍使用匿名请求/成功响应，后续增加 DTO 时只能做向后兼容扩展。

## 认证

`/auth/login`、`/auth/time`、`/auth/check` 和 `/health/ready` 免认证，其余接口使用 `Authorization: Bearer <token>`。运维脚本也可使用 Basic Auth，但不建议跨不可信网络传输。控制端口不应直接暴露到公网。

## 错误模型

基础设施、认证和插件代理错误使用统一信封：

```json
{
  "success": false,
  "message": "未授权访问",
  "error": {
    "code": "unauthorized",
    "message": "未授权访问",
    "traceId": "0H...:00000001",
    "details": null
  }
}
```

`message` 为旧前端兼容字段；新客户端应按稳定的 `error.code` 分支，并把 `traceId` 用于日志定位。服务端不会在响应中返回堆栈、磁盘路径或密钥。历史业务端点的参数错误可能暂时只有 `success/message`，客户端必须兼容 `error` 缺省。

常用状态码：`400` 输入无效，`401` 未认证，`403` 无权限，`404` 资源不存在，`409` 状态冲突，`413` 请求体过大，`429` 超过限额，`503` 服务或插件不可用。

## 插件代理

插件不得注册 `/api/web-tamper/*` 一类核心业务路由。统一入口为 `/api/v1/plugins/{pluginId}/proxy/{pluginPath}`。

代理入口复用控制台 JWT/Basic 鉴权，并按“用户 + 插件”限频；所有插件共享并发上限。每次调用和拒绝都写审计日志，记录用户、来源 IP、插件、相对路径、结果和耗时。

```ly
Plugins {
    ApiProxy {
        MaxRequestBodyBytes = 1048576
        RequestsPerMinute = 120
        MaxConcurrentRequests = 16
    }
}
```

已知 `Content-Length` 超限时返回 `413/plugin_request_too_large`；频率或并发超限返回 `429`，并在适用时返回 `Retry-After`。插件仍需对流式或未声明长度的正文设置自己的读取边界。

## 响应缓存

`GET /api/v1/response-cache` 返回当前节点的缓存元数据，不读取磁盘正文。`POST /api/v1/response-cache/purge` 按 `key`、`host` 和 `path` 前缀的交集清理条目，请求至少提供一个条件：

```json
{ "host": "example.com", "path": "/assets/" }
```

空条件返回 `400/invalid_purge_filter`，以免误删当前节点的全部缓存。多节点部署需要通过节点级操作逐个清理；该接口不会隐式广播。

## 兼容检查

后端契约测试位于 `tests/LyWaf.Tests/ManagementApiContractTests.cs` 和 `PluginApiAdmissionGateTests.cs`，覆盖版本映射、错误信封、OpenAPI 引用和插件限额。前端只有一个 Axios 基址 `/api/v1`，插件客户端由 `pluginProxyApi` 派生；`npm run build` 检查调用类型和公共响应字段兼容性。

```powershell
dotnet test tests/LyWaf.Tests/LyWaf.Tests.csproj --no-restore
cd Frontend
npm run build
```
