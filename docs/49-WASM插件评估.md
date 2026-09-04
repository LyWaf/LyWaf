# WASM 插件评估

## 当前结论

LyWaf 暂不内置 WASM 运行时，继续以 .NET `ILyWafPlugin` 作为主要扩展方式。当前插件与宿主同为 .NET，能够直接使用 DI、强类型配置、ASP.NET Core 中间件、YARP 上下文、事件总线和后台任务协调器；引入 WASM 不会自动提升这些现有插件的可靠性，反而会增加 ABI、内存复制、运行时补丁和调试成本。

只有出现以下至少一个真实需求时才重新评估：

- 需要让 Rust、Go、C/C++ 等非 .NET 团队发布可移植过滤器。
- 必须运行来源不完全可信、不能获得宿主进程任意代码权限的扩展。
- 同一插件需要跨 Windows/Linux 和不同 LyWaf 版本稳定部署，且能接受受限宿主 API。

WASM 沙箱不是完整安全边界。运行时漏洞、宿主调用、共享内存、资源耗尽和业务授权仍需独立控制，不能因为模块格式是 WASM 就视为可信。

## 必需 ABI

首版 ABI 应只传输有限、版本化的数据结构，不能暴露 `HttpContext`、`IServiceProvider` 或任意 .NET 对象句柄：

1. `plugin_init(abi_version, config_handle)`：验证 ABI 与只读配置。
2. `on_request(headers_handle, body_chunk_handle)`：返回 continue、reject 或有限的 Header 修改。
3. `on_response(headers_handle, body_chunk_handle)`：返回 continue 或有限响应修改。
4. `plugin_tick(unix_milliseconds)`：可选、受调度配额的后台回调。
5. `plugin_shutdown()`：释放模块内状态。

Host 与 Guest 使用长度前缀字节缓冲区，不共享原始指针。Header 名称、数量、单值长度、正文块和返回数据均有硬上限。ABI 采用独立主版本；宿主只承诺同一 ABI 主版本内向后兼容。

## 资源配额

每个模块至少需要以下独立限制：

- 最大线性内存页数，禁止运行时无限增长。
- 每次回调 fuel/指令预算和墙钟超时。
- 每请求最大宿主调用次数与日志字节数。
- 后台任务并发数、队列容量和执行周期下限。
- 模块实例池上限；超限时按 Fail Open/Closed 的 Route 策略处理。

超时或 trap 必须终止该次调用、记录固定低基数指标，并按错误阈值临时隔离模块。模块不能让代理线程无限等待。

## 宿主权限

默认只开放读取有限请求元数据、记录结构化日志和返回决策。网络、文件、时钟、随机数、环境变量、DNS、密钥和插件间调用全部默认拒绝，通过 manifest 逐项授权。

文件权限只能映射插件专属数据目录，路径解析必须阻止符号链接逃逸。网络权限应使用域名/端口 allowlist、DNS 重绑定防护、超时、响应上限和并发限制，不能直接开放通用 socket。敏感 Header 和正文访问必须由 Route 与插件 manifest 双重允许。

## 与 .NET 插件并存

WASM 模块未来应通过一个受信任的 .NET `WasmHostPlugin` 接入现有生命周期、统一插件 API、管理页面、指标和任务协调器，而不是建立第二套插件管理器。安装包必须包含模块摘要、签名、ABI 版本、权限 manifest 和最低 LyWaf 版本；控制台在启用前展示权限差异。

重新评估时先完成基准：测量无操作过滤器以及读取 Header、读取正文块、修改响应三种场景的 P50/P95/P99、CPU 和内存，并与等价 .NET 插件对照。没有隔离需求和可接受开销数据时，不把 WASM 作为功能数量指标加入核心。
