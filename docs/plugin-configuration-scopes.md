# 插件分层配置：全局、站点与路径

LyWaf 借鉴 Nginx 的加载声明和分层配置思想，保留 `.ly` 语法。可直接解析的配置片段见 [plugin-scopes.ly](examples/plugin-scopes.ly)。将片段合并到现有配置，不要删除已有监听、上游和代理路由。

## 加载与启用分开

```text
Plugins {
    Load = ["analysis", "web-tamper", "image-process"]
    image-process {
        Enabled = false
        DefaultQuality = 85
        MaxWidth = 4096
    }
}
```

- 显式 `Load` 是完整插件清单，未列出的插件不会被注册和启用。包括原本默认开启的安全插件，因此部署时必须列全需要的插件。发现阶段仍可能读取程序集及创建元数据实例，它不是程序集安全隔离机制。
- `Load` 中插件进入生命周期管理，图片处理的 `Enabled` 则控制每个请求是否处理。这样全局关闭、单站点开启才可生效。
- `EnabledOverrides` 和 `DisabledPlugins` 仍是生命周期开关；后台彻底禁用插件后，任何站点都不能重新开启它。
- 不写 `Load` 时保留既有发现/启用逻辑。修改 `Load` 必须重启；不能通过配置重载安装新插件。

## 分层覆盖

```text
Sites {
    images {
        Hosts = ["img.example.com"]
        Plugins {
            image-process {
                Enabled = true
                DefaultQuality = 80
            }
        }
        Routes {
            thumbnails {
                Path = "/thumb/**"
                Plugins {
                    image-process {
                        DefaultQuality = 60
                        MaxWidth = 800
                        CacheSeconds = 0
                        SupportedFormats = ["png"]
                    }
                }
            }
        }
    }
}
```

优先级：插件默认值 → 全局 → 匹配站点 → 匹配路径。没有填写才继承；`false`、`0`、空字符串和空集合均是显式值，不代表继承。数组和字典整体替换，普通对象按字段合并。

| 请求 | Enabled | DefaultQuality | MaxWidth | CacheSeconds |
| --- | --- | --- | --- | --- |
| 其他域名 | false | 85 | 4096 | 默认值 |
| img.example.com/a.png | true | 80 | 4096 | 默认值 |
| img.example.com/thumb/a.png | true | 60 | 800 | 0 |

`Sites`/`Routes` 在这里是**插件作用域选择器**，不是代理路由声明，不会新增监听、上游或修改 YARP 的路由选择。现有站点管理、转发路由继续单独配置。

匹配规则：

1. `Hosts` 支持完整主机名和 `*.example.com` 后缀匹配，不写协议/端口；实际请求端口不参与选择。精确域名优先，再选最长匹配后缀；通配后缀不匹配根域名。不同站点不能重复声明同一个模式。
2. `Path` 支持精确路径和尾部 `/**`，路径区分大小写。`/thumb/**` 匹配 `/thumb` 和 `/thumb/a.png`，不匹配 `/thumbnail`。
3. 多个路径匹配时选择最长路径；同等长度精确匹配优先。同一站点不能重复声明相同路径。
4. 只继承被选中的一条站点/路径链，不叠加多个匹配站点或多个匹配路径。

## 生效配置查询

插件管理中打开图片处理插件的配置面板，在“生效配置”输入域名与路径并查询，可看到字段值和 `default`、`global`、`site:...`、`route:...` 来源。

接口：`GET /api/v1/plugins/image-process/config/effective?host=img.example.com&path=/thumb/a.png`。需要原有后台权限，敏感值不返回。结果表示当前已发布的配置，不是编辑框里尚未保存的内容，也不表示插件生命周期已启用。

局部覆盖目前在 `config.ly` 或已有配置编辑器中维护；本次未增加站点/路径可视化编辑表单。

## 插件开发接入

插件必须声明 `Metadata.SupportsScopedConfiguration = true`，并在请求处理中从 DI 获取 `IPluginRequestConfiguration`：

```csharp
var options = resolver.Get<MyOptions>(httpContext, "my-plugin");
```

每次返回独立配置对象，不能修改其他请求的配置。同一请求首次读取后固定使用同一个配置快照。不要把局部参数绑定到全局单例字段，否则并发请求会互相污染。

声明只允许全局配置的字段：

```csharp
[PluginConfiguration(Scopes = PluginConfigurationScope.Global)]
public string StorageDirectory { get; set; } = "data";
```

属性默认允许三个作用域；`RequiresRestart` 字段不允许站点/路径覆盖。模型须有无参构造函数和公开可读写属性，默认值放在模型初始化器中。正式配置键名遵循 `ConfigurationKeyName`。

**当前图片处理插件已完整接入并通过实际图片缩放测试。** 其他插件不能仅加开关就认为完成迁移：后台任务、全局状态、存储目录等需要单独设计。未声明支持的插件在局部配置块中会被拒绝，避免配置静默无效。

## 校验与重载边界

启动时校验所有已接入插件的全局和局部合并结果，包括未知字段、类型、范围、作用域以及域名/路径声明。失败则拒绝启动。

配置重载时先构建完整的插件配置候选快照，全部通过后一次替换；失败保留上一份插件快照，并在查询结果 `lastReloadError` 和日志中报告原因。正在处理的请求继续使用旧快照。

这只覆盖该分层解析器管理的插件请求配置，**不保证整个应用的配置、生命周期、其他业务 API 或代理路由原子发布**。部署仍须保留配置备份。当前没有持久化历史快照和跨进程回滚机制。

测试：`PluginScopedConfigurationTests` 覆盖真实 `.ly` 示例加载、继承、数组替换、启用状态、路径边界、无效重载保留旧快照、请求快照一致性和图片处理中间件实际输出。
