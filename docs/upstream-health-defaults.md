# 上游主动健康检查

## 控制台配置

进入站点管理，点击站点右侧的“健康检查”。设置按上游集群保存，共用同一集群的站点共用检查条件。

可配置启停、探测路径、HTTP 方法、请求数据、Content-Type、健康状态码、间隔、超时及连续成功/失败次数。支持 JSON、表单和纯文本请求体；状态码可填 `200`、`200,204` 或 `2xx`。多个状态码之间为“或”关系。POST/PUT/DELETE 等探测会反复调用接口，应使用无副作用的专用检查接口。

例如：路径 `/ready`，方法 `POST`，类型 `application/json`，数据 `{"probe":true}`，健康状态码 `204`。此时仅返回 204 才算成功，200 也会计为失败。

配置通过集群补丁持久化并重载，不覆盖站点路由或被动检查配置；修改要求管理权限和现有的身份再次验证。GET/PUT 接口为 `/api/lb/clusters/{clusterId}/health-check`，业务实现独立放在 `Control/Endpoints/ControlApi.UpstreamHealth.cs`。

## 文件配置

`config.ly` 可在站点内使用简写（也可放入 `proxy { ... }` 内）：

```text
localhost {
    proxy = ["http://127.0.0.1:8080"]
    health_check {
        enabled = true
        path = "/api/health"
        method = POST
        content_type = "application/json"
        body = "{\"probe\":true}"
        status_codes = [200, "20x", "30x"]
        interval = 30
        timeout = 5
        passes = 2
        fails = 2
    }
}
```

时间单位为秒。`status_codes` 支持数组或逗号分隔字符串，每个 `x` 独立代表一位 0–9：`20x` 是 200–209，`30x` 是 300–309，`2xx` 是 200–299。带通配符的值建议加引号。匹配范围只限合法的 HTTP 状态码 100–599；多个模式为“或”关系。

简写会转为 YARP HealthCheck 和 LyxActiveHealth Metadata。未知字段和无效值会报配置错误，不应将简写与原生 Active/Passive 块混用。控制台保存的集群补丁可能覆盖文件配置，修改文件后需检查是否存在旧补丁。

YAML 集群中可直接指定以下配置（Metadata 与 HealthCheck 同级）：

```yaml
HealthCheck:
  Active:
    Enabled: true
    Policy: LyxActiveHealth
    Path: /ready
    Interval: 00:00:30
    Timeout: 00:00:05
Metadata:
  LyxActiveHealth.Method: POST
  LyxActiveHealth.Body: '{"probe":true}'
  LyxActiveHealth.ContentType: application/json
  LyxActiveHealth.AvalidCode: '204'
  LyxActiveHealth.Passes: '2'
  LyxActiveHealth.Fails: '2'
```

`AvalidCode` 为现有配置键的拼写，需原样使用。网络失败及超时默认不会算作健康。配置了响应内容或响应头检查时，这些既有条件仍会参与判断，控制台本次不编辑它们。

## 默认行为

未配置主动健康检查的集群默认启用 `LyxActiveHealth`，不依赖业务请求触发。默认检查路径为 `/`，检查间隔为 30 秒，单次请求超时为 5 秒。配置过滤器对内存配置、文件配置及托管站点统一补齐默认值，不改写原始配置文件。

默认连续两次成功后标记正常，连续两次失败后标记异常。成功默认要求 HTTP 2xx；首次结果尚未满足阈值时显示等待探测。异常节点会参与 YARP 的健康节点筛选，因此应给需要鉴权或依赖特定 Host 的上游配置适合的健康检查端点，避免将首页的 401、404 等响应误认为服务故障。

以下为集群内的 YAML 配置示例，可覆盖默认检查路径和时间：

```yaml
HealthCheck:
  Active:
    Enabled: true
    Policy: LyxActiveHealth
    Path: /health
    Interval: 00:00:30
    Timeout: 00:00:05
```

显式设置 `HealthCheck.Active.Enabled: false` 可关闭主动探测。已有的策略、路径、间隔、超时及被动检查配置均保留；被动检查不会自动启用。升级程序后需要重启以加载新的默认配置过滤器。

系统保留的 `cluster_unuse` 是本地响应场景的占位集群，不是真实上游，始终不进行主动检查，包括旧版生成配置。它的 `0.0.0.0` 占位地址不能用于网络请求；真实上游应填写可连接的 IP 或域名，不应填写监听用的通配地址。

实现入口：`Policy/DefaultActiveHealthConfigFilter.cs`；探测请求及结果判断：`Policy/ActiveHealth.cs`。自动化测试使用本机临时上游验证无业务流量时的探测、故障及恢复，不访问生产上游。
