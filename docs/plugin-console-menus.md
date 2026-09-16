# 插件控制台菜单

插件通过 `GetActions()` 返回 `Type = "page-link"` 的动作注册页面菜单。控制台从 `/api/v1/plugins/menus` 读取菜单，侧栏不再硬编码具体插件。接口沿用管理端身份和权限检查。

```csharp
public override IReadOnlyList<PluginAction> GetActions() =>
[
    new PluginAction
    {
        Id = "cc",
        Name = "CC 防护",
        Type = "page-link",
        Path = "/plugins/analysis/cc"
    }
];
```

- 只有 Running/Initialized 插件显示菜单。插件可以根据自身配置决定返回哪些页面动作。
- 路径必须位于 `/plugins/{插件ID}` 下，不接受外部地址、父目录跳转或编码路径。
- 前端页面在 `Frontend/src/plugins/{插件ID}/plugin.ts` 注册路由，通过模块扫描加入控制台；注册菜单并不会自动生成任意页面组件，新增前端模块需重新构建。
- 插件管理中启停后立即刷新菜单，另有 15 秒刷新用于反映其他会话和配置变更。禁用当前页面所属插件后跳转到插件管理。
- CC 页面由 analysis 模块提供，地址为 `/plugins/analysis/cc`，旧 `/cc-protection` 跳转到新地址。当前仍复用已有 CC 业务接口；本次没有迁移 CC 检测中间件和业务服务。
- 网页防篡改复用现有 page-link 注册，自动出现在插件菜单中。没有页面动作的插件不显示子菜单。

菜单隐藏不是后端访问控制。插件自己的 API 仍必须通过插件代理与权限校验；共享业务接口沿用原有管理端权限。
