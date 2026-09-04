# 插件 SDK 与契约包

## 为什么拆分

外部插件不应引用 `LyWaf.dll`。该程序集包含 Web 宿主、数据库和代理实现，任何内部重构都可能造成不必要的二进制耦合。`sdk/LyWaf.PluginSdk` 现在只发布插件生命周期、上下文、事件、后台任务和统一 API 处理契约，宿主实现仍留在主程序。

## 创建插件

复制 `sdk/templates/LyWaf.Plugin.Template`，修改程序集名和 `PluginMetadata.Id`，然后引用稳定包：

```xml
<PackageReference Include="LyWaf.PluginSdk" Version="1.0.0" />
```

插件至少继承 `LyWafPluginBase` 并提供 `Metadata`。需要请求管道扩展时重写 `ConfigureMiddleware` 或 `ConfigureProxyPipeline`；需要控制台 API 时实现 `IPluginApiHandler`。编译后的插件 DLL 及其私有依赖放入配置的插件目录，不要复制宿主提供的 `LyWaf.PluginSdk.dll`。

## 契约范围

- 生命周期：`ILyWafPlugin`、`LyWafPluginBase`、`PluginState` 和 `PluginMetadata`。
- 宿主能力：`IPluginContext`、`IPluginManager`、`IPluginTaskCoordinator`。
- 通信：`IPluginEventBus` 和内置事件 DTO。
- 页面与 API：`PluginAction`、`PluginActionResult`、`IPluginApiHandler`。

SDK 只保证这些公开契约。插件不得依赖 `Services`、`Config` 或宿主中具体实现类；通过 `IPluginContext.Services` 获取的非 SDK 类型也不属于兼容承诺。

## 版本与废弃策略

SDK 使用语义化版本。`1.x` 内保持现有公开成员的源码和二进制兼容；次版本可以新增可选成员或带默认实现的接口成员。删除成员、修改签名或改变既有语义只能进入新的主版本。

计划废弃的 API 会先标记 `ObsoleteAttribute`，至少保留一个次版本且不少于六个月。安全原因需要紧急移除时，会在发布说明中明确影响、替代接口和迁移方法。

## 构建与打包

```powershell
dotnet build sdk/LyWaf.PluginSdk/LyWaf.PluginSdk.csproj
dotnet pack sdk/LyWaf.PluginSdk/LyWaf.PluginSdk.csproj -c Release -o artifacts/packages
```

包包含 XML API 文档。发布前还应编译模板项目，并确认 SDK 程序集不反向引用 `LyWaf.dll`。
