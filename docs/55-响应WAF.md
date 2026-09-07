# 通用响应 WAF

响应 WAF 检查已经通过请求侧安全策略并由上游、静态文件或内部处理器生成的响应。它与网页防篡改不同：防篡改比较指定资源的历史基线，响应 WAF 按状态、Header 和内容规则识别单次泄露。

## 管线位置

响应 WAF 位于响应缓存内侧和响应压缩外侧。响应先检查或脱敏，再写入缓存，最后按客户端能力压缩。因此缓存不会保存未脱敏内容，压缩也不会阻碍普通未压缩响应的检测。

上游已经返回 Gzip、Deflate 或 Brotli 时，LyWaf 会在大小限制内解压一份用于检查。`Block` 可以替换这类响应；`Redact` 不会重写已有压缩流，而是保留原响应并记录命中，避免生成损坏内容。

## 内置检测

- .NET/Python 等错误堆栈；
- SQLSTATE、Oracle、PostgreSQL、MySQL、SQL Server 和 SQLite 错误；
- API Key、Access Token、Client Secret、密码和私钥头；
- 可选邮箱和国内手机号检测。

自定义规则的 `Field` 支持 `Status`、`Header` 和 `Body`；Header 规则还需填写 `HeaderName`。操作符支持 `Equal`、`Contains`、`StartsWith`、`EndsWith` 和带 100ms 超时的 `Regex`。

## 动作

- `Observe`：记录 `ResponseWaf.Finding` 和警告日志，原响应不变。
- `Block`：丢弃暂存响应，删除 Content-Encoding、Content-Length 和 ETag，返回配置的错误状态和规则 ID。
- `Redact`：对未压缩文本中的凭据、邮箱和手机号替换为固定占位符并删除 ETag。

## 内存与流式边界

`MaxInspectBytes` 默认 256 KiB，允许范围为 1 KiB 到 4 MiB。超过上限后，已暂存内容立即写给客户端，后续内容直通，不再宣称完整检查。WebSocket、HEAD、SSE、gRPC 和主动流式 Flush 会旁路内容检查。大文件不会完整进入托管内存。

生产环境应先用 `Observe` 收集误报，再针对稳定规则使用 `Block` 或 `Redact`。隐私数据检测默认关闭，因为普通联系人列表也可能包含大量合法邮箱和手机号。
# 后台管理

`GET /api/response-waf` 返回当前响应 WAF 配置，`PUT /api/response-waf` 更新开关、动作、检查上限和自定义规则。后台会在写盘前验证动作、状态码、字段、操作符、规则 ID 唯一性和正则表达式，失败不会改变运行配置。
