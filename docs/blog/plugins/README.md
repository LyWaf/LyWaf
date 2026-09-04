# LyWaf 插件开发：从后端扩展到控制台页面

本专栏按一条完整功能的落地顺序讲解 LyWaf 插件系统。读者最终会得到一个具有配置、代理中间件、控制台 API、后台任务和 Vue 管理页面的插件。

## 阅读顺序

1. [插件系统架构与边界](01-插件系统架构.md)
2. [新建后端插件](02-新建后端插件.md)
3. [生命周期、依赖注入与启停](03-生命周期与依赖注入.md)
4. [config.ly 配置与插件数据目录](04-配置与数据目录.md)
5. [把中间件嵌入代理管线](05-代理中间件插件.md)
6. [统一插件 API 与后台转发](06-插件API与后台转发.md)
7. [嵌入控制台页面](07-嵌入控制台页面.md)
8. [插件管理页、操作与页面入口](08-插件管理与操作.md)
9. [后台任务与插件事件总线](09-后台任务与事件总线.md)
10. [完整全栈示例、测试与发布](10-完整示例与排障.md)

## 最终目录

```text
Plugins/MyPlugin/
├── MyPlugin.cs
├── MyPluginMiddleware.cs
├── MyPluginOptions.cs
└── MyPluginService.cs

Frontend/src/plugins/my-plugin/
├── plugin.ts
├── api.ts
└── MyPluginPage.vue
```

后端插件负责能力和数据，前端插件负责页面。两者使用相同的插件 ID，并通过统一插件代理 API 通信。

## 适用读者与准备工作

读者应了解 ASP.NET Core DI、中间件和 Vue Router，并已完成主专栏中的首次启动。阅读示例时建议同时打开以下源码：

- `Plugins/Core/ILyWafPlugin.cs`：插件契约、元数据、状态和基类。
- `Plugins/Core/IPluginManager.cs`：发现、排序、初始化、启停和外部 DLL 加载。
- `Plugins/Core/PluginExtensions.cs`：宿主注册、统一 API 和两段代理管线。
- `Plugins/Sys/WebTamper/WebTamperPlugin.cs`：完整内置插件实例。
- `Frontend/src/plugins/index.ts`：前端插件路由自动发现。
- `Frontend/src/views/PluginManagement.vue`：插件列表、配置和 Action 展示。

## 推荐实践方式

每篇完成后都编译一次后端；涉及前端时再执行前端构建。示例插件 ID 始终使用 `my-plugin`，这样配置节、后端 API、前端目录和路由能够直接对应。正式开发时将其替换为稳定、全小写、以连字符分隔的 ID。

```powershell
dotnet build
cd Frontend
npm run build
```

本专栏描述的是当前仓库实现，而不是抽象插件规范。尤其需要记住：后端外部 DLL 支持独立加载，前端页面仍属于控制台的构建期模块。
