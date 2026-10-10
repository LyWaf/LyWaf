# 插件配置规范与预校验

通用后台编辑入口统一字段描述与校验。已接入插件还支持[全局、站点与路径分层配置](plugin-configuration-scopes.md)。

## 配置描述

`GET /api/v1/plugins/{pluginId}/config` 保留原有 `config`、`defaults`、`labels`、`fieldDefaults`，新增 `schema`。描述来自 `Metadata.DefaultOptions` 的类型，而非猜测当前值。

每个字段包含路径、类型、标签、Required、范围下限/上限、Sensitive、RequiresRestart。嵌套集合使用 `*` 路径段。标签复用 Description，约束复用 DataAnnotations；正式键名复用 ConfigurationKeyName。

```csharp
[Range(1, 100)]
[Description("输出质量")]
public int Quality { get; set; } = 85;

[PluginConfiguration(Sensitive = true)]
public string Credential { get; set; } = "";

[PluginConfiguration(RequiresRestart = true)]
public string Directory { get; set; } = "data";
```

`PluginConfigurationAttribute` 位于 SDK 编入的契约源文件中。敏感标记也识别 PasswordPropertyText 和常见敏感字段名，但命名推断不完整，插件应显式声明。

## 校验与保存

`POST /api/v1/plugins/{pluginId}/config/validate` 接收与保存接口相同的扁平字符串字典，仅检查候选配置，不保存、不重载、不修改运行实例。

保存入口使用同一个候选准备与校验流程；校验失败返回 400 和 `error.details` 字段错误，先于写补丁执行。后台增加“校验配置”，数字输入使用 schema 的类型及范围，并显示服务端字段错误。

保存现在必须携带读取配置时返回的版本，使用 `If-Match` 请求头。已接入分层配置的插件还会校验完整作用域树并核对发布结果，详见[插件配置发布与并发保护](plugin-configuration-publication.md)。

检查内容包括未知键、重复大小写键、非法集合索引、标量转换、未定义枚举和递归 DataAnnotations。字段数限制为 2048，路径最大 512 字符、12 段，单值最大 65536 字符。仅接受可读写的正式属性，不接受只写别名属性，避免绑定器静默忽略输入。

候选对象独立创建；传入集合先清除候选默认集合，再绑定，避免默认列表与提交列表重复追加。

## 敏感信息与重启边界

- 通用查询从当前值、默认值及集合元素默认值中删除敏感字段值。
- 通用保存保留运行实例中的敏感字段，不允许通过此入口修改它们；修改凭据应使用插件专用接口或部署配置。
- RequiresRestart 字段变化会拒绝在线保存；含有这类嵌套集合字段的模型采取保守拒绝策略。当前没有“保存后待重启”的配置暂存机制。
- 图片处理配置已经为质量、宽高和缓存时间补充范围约束。

## 尚未覆盖的范围

这不是整套配置系统的原子发布实现。`config.ly` 直接加载、插件专用业务 API、多来源配置合并仍有各自的校验路径，本阶段未统一改造。通用后台提交完整可编辑配置，不能将预校验接口当作任意局部补丁的最终有效配置模拟器。

站点/路径作用域、继承和来源展示已在分层解析器中实现，图片处理插件已接入；其 `config.ly` 配置会经过合并后校验。其他插件仍需逐个迁移。应用级版本化发布与持久化回滚尚未实现，也不替代插件业务入口的必要校验。

相关测试：`PluginConfigurationSchemaTests` 覆盖类型、范围、未知键、集合、敏感保留与不修改运行实例；前端 `api-contract.test.mjs` 验证预校验请求没有调用保存接口。
