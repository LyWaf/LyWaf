# LyWaf 插件系统

LyWaf 提供了一个灵活的插件系统，允许开发者扩展 WAF 的功能而无需修改核心代码。

## 📁 目录结构

```
Plugins/
├── Core/                    # 插件核心框架
│   ├── ILyWafPlugin.cs      # 插件基础接口
│   ├── IPluginContext.cs    # 插件上下文接口
│   ├── IPluginEventBus.cs   # 事件总线接口
│   ├── IPluginManager.cs    # 插件管理器
│   └── PluginExtensions.cs  # 扩展方法
├── Examples/                # 示例插件
│   ├── RequestLoggerPlugin.cs   # 请求日志示例
│   └── CustomHeaderPlugin.cs    # 自定义头示例
├── Templates/               # 插件模板
│   └── PluginTemplate.cs.template
└── README.md
```

## 🚀 快速开始

### 1. 创建插件

继承 `LyWafPluginBase` 基类：

```csharp
using LyWaf.Plugins.Core;

public class MyPlugin : LyWafPluginBase
{
    public override PluginMetadata Metadata => new()
    {
        Id = "my-plugin",
        Name = "我的插件",
        Version = "1.0.0",
        Description = "这是一个示例插件",
        Priority = PluginPriority.Normal
    };
    
    public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
    {
        proxyApp.Use(async (context, next) =>
        {
            // 你的逻辑
            await next(context);
        });
    }
}
```

### 2. 配置插件

在 `appsettings.yaml` 中配置：

```yaml
Plugins:
  # 全局插件配置
  Enabled: true
  PluginDirectory: plugins
  DataDirectory: plugin_data
  DisabledPlugins:
    - some-plugin-id
  
  # 各插件的配置
  my-plugin:
    Enabled: true
    CustomSetting: value
```

或在 `config.ly` 中：

```
plugins {
    enabled = true
    plugin_directory = "plugins"
    
    my-plugin {
        enabled = true
        custom_setting = "value"
    }
}
```

## 📚 核心概念

### 插件生命周期

```
发现 → ConfigureServices → InitializeAsync → ConfigureMiddleware → StartAsync
                                                                       ↓
                                                                   运行中
                                                                       ↓
                                                                  StopAsync
```

### 插件优先级

```csharp
public enum PluginPriority
{
    Highest = 0,    // 最高（安全相关）
    High = 100,     // 高
    Normal = 500,   // 正常（默认）
    Low = 900,      // 低
    Lowest = 1000   // 最低
}
```

优先级决定了：
- 服务配置顺序
- 中间件注册顺序
- 启动/停止顺序

### 插件上下文 (IPluginContext)

插件上下文提供与主程序交互的能力：

```csharp
public override Task InitializeAsync(IPluginContext context)
{
    // 获取配置
    var options = context.GetPluginConfig<MyOptions>();
    
    // 服务目录仅提供当前插件的受限能力，不是宿主根容器
    var storage = context.Services.GetRequiredService<IPluginStorage>();
    
    // 使用日志
    context.Logger.Info("插件已初始化");
    
    // 发布事件
    await context.PublishEventAsync(new MyEvent());
    
    // 订阅事件
    context.SubscribeEvent<RequestStartedEvent>(async e => {
        // 处理事件
    });
    
    // 访问数据目录
    var dataPath = Path.Combine(context.DataDirectory, "my-data.json");
    
    return base.InitializeAsync(context);
}
```

### 事件总线

插件间通过事件总线进行松耦合通信：

```csharp
// 发布事件
await context.PublishEventAsync(new MyCustomEvent { Data = "hello" });

// 订阅事件
var subscription = context.SubscribeEvent<MyCustomEvent>(async e => {
    Console.WriteLine(e.Data);
});

// 取消订阅
subscription.Dispose();
```

内置事件：
- `RequestStartedEvent` - 请求开始
- `RequestCompletedEvent` - 请求完成
- `ConfigurationChangedEvent` - 配置变更
- `PluginStateChangedEvent` - 插件状态变更

## 🔌 插件类型

### 中间件插件

在请求管道中处理请求：

```csharp
public override void ConfigureProxyPipeline(IApplicationBuilder proxyApp)
{
    proxyApp.UseMiddleware<MyMiddleware>();
}
```

### 服务插件

注册和提供服务：

```csharp
public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<IMyService, MyService>();
}
```

### 后台任务插件

运行后台任务：

```csharp
private CancellationTokenSource? _cts;

public override Task StartAsync(CancellationToken cancellationToken)
{
    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _ = BackgroundTaskAsync(_cts.Token);
    return base.StartAsync(cancellationToken);
}

public override async Task StopAsync(CancellationToken cancellationToken)
{
    _cts?.Cancel();
    await base.StopAsync(cancellationToken);
}
```

## 📦 外部插件

外部插件可以编译为独立的 DLL 放入 `plugins` 目录：

1. 创建类库项目
2. 引用 LyWaf 或 LyWaf.Plugins.Core
3. 实现 `ILyWafPlugin` 接口
4. 编译并复制 DLL 到 `plugins` 目录

外部插件支持：
- 热重载（需启用 `EnableHotReload`）
- 独立卸载（使用独立的 AssemblyLoadContext）

## 🔧 API 参考

### ILyWafPlugin

| 方法 | 说明 |
|------|------|
| `ConfigureServices` | 配置 DI 服务 |
| `InitializeAsync` | 初始化插件 |
| `ConfigureMiddleware` | 配置全局中间件 |
| `ConfigureProxyPipeline` | 配置代理管道中间件 |
| `StartAsync` | 启动插件 |
| `StopAsync` | 停止插件 |

### IPluginManager

| 方法 | 说明 |
|------|------|
| `GetPlugin(id)` | 获取指定插件 |
| `GetAllPlugins()` | 获取所有插件 |
| `EnablePluginAsync(id)` | 启用插件 |
| `DisablePluginAsync(id)` | 禁用插件 |
| `ReloadPluginAsync(id)` | 重载插件 |
| `GetPluginState(id)` | 获取插件状态 |

## 📝 最佳实践

1. **保持插件独立** - 避免插件间的强依赖
2. **使用事件通信** - 通过事件总线进行插件间通信
3. **妥善处理异常** - 插件异常不应影响主程序
4. **合理设置优先级** - 安全相关的插件使用高优先级
5. **支持配置** - 使用 Options 模式支持配置
6. **记录日志** - 使用 `context.Logger` 记录日志
7. **清理资源** - 在 `StopAsync` 中清理所有资源

## 📄 示例插件

查看 `Examples` 目录中的示例：

- **RequestLoggerPlugin** - 请求日志记录
- **CustomHeaderPlugin** - 自定义响应头

使用模板快速创建新插件：`Templates/PluginTemplate.cs.template`
