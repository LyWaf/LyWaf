# LyWaf 中间件逐篇解析

本子专栏严格按照 `Program.cs` 注册顺序排列。建议先阅读 [中间件总览](../06-中间件管线详解.md)，再逐篇深入。

## 全局管线

1. [WebSocket 入口阶段](01-WebSocket入口.md)
2. [BasePluginMiddleware：插件事件总线入口](02-BasePluginMiddleware.md)
3. [AcmeChallengeMiddleware：证书验证短路](03-AcmeChallengeMiddleware.md)

## 代理外层与响应包装

4. [BandwidthCountingMiddleware：流量统计](04-BandwidthCountingMiddleware.md)
5. [CorsMiddleware：Origin 与预检](05-CorsMiddleware.md)
6. [ResponseCompressMiddleware：响应压缩](06-ResponseCompressMiddleware.md)
7. [WebTamperMiddleware：网页安全基线](07-WebTamperMiddleware.md)

## 安全检查

8. [AccessControlMiddleware：IP、地理和连接限制](08-AccessControlMiddleware.md)
9. [IpLogMiddleware：指定 IP 深度日志](09-IpLogMiddleware.md)
10. [AutoHttpsMiddleware：HTTPS 重定向](10-AutoHttpsMiddleware.md)
11. [WafControlMiddleware：核心 WAF 检测](11-WafControlMiddleware.md)
12. [CaptchaMiddleware：人机验证](12-CaptchaMiddleware.md)
13. [CcProtectionMiddleware：CC 请求侧处置](13-CcProtectionMiddleware.md)

## 流量与响应观察

14. [ThrottledMiddleware：响应带宽节流](14-ThrottledMiddleware.md)
15. [SpeedLimitMiddleware：请求速率限制](15-SpeedLimitMiddleware.md)
16. [CcResponseMonitorMiddleware：错误响应反馈](16-CcResponseMonitorMiddleware.md)
17. [插件代理阶段：优先级与插入点](17-插件代理阶段.md)

## 本地响应与上游

18. [SimpleResMiddleware：固定和 Mock 响应](18-SimpleResMiddleware.md)
19. [FileProviderMiddleware：静态文件服务](19-FileProviderMiddleware.md)
20. [LoadBalancing：YARP 上游选择阶段](20-LoadBalancing.md)
21. [PortMatcherPolicy：进入中间件前的路由筛选](21-PortMatcherPolicy.md)

## 每篇统一分析维度

- 在完整管线中的位置。
- 请求进入后的实际处理流程。
- 何时调用下游，何时直接短路。
- 响应返回阶段执行什么。
- 配置和运行数据来自哪里。
- 为什么必须放在当前位置。
- 性能、安全风险和排障方法。

## 实践建议

阅读时使用一个可识别上游实例的测试站点，逐个开启功能并保留请求、响应和日志。每次只改变一个配置变量，才能判断短路发生在哪一层。

中间件改动提交前至少回归：普通代理、HTTPS 重定向、CORS 预检、WAF 拒绝、限流、静态文件、WebSocket、插件启停和上游故障。涉及响应流包装时再增加大文件、取消和异常测试。

## 源码定位

核心实现位于 `Middleware/`，组合顺序位于 `Program.cs`，插件插入点位于 `Plugins/Core/PluginExtensions.cs`，路由筛选位于 `Policy/`。文档顺序应随组合根变化同步更新，不能只根据文件名推断实际执行顺序。
