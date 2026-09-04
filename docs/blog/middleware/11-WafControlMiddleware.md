# WafControlMiddleware：核心 WAF 检测

## 位置

位于 AutoHttps 之后、Captcha 和 CC 之前。HTTP 重定向已经结束，进入这里的请求才值得执行内容检测。

## 前置工作

- 按 Domain 和 Path 决定是否注入域名日志器。
- 从 ProtectOptions 设置请求体最大值。
- 需要记录命中详情时，使用 CaptureStream 包装请求和响应流。

## 检测顺序

1. 检查 IP 是否已被封禁，命中后返回封禁页面。
2. 检查统计白名单路径，命中则跳过 WAF 并继续。
3. 检查 Query/Args 攻击特征。
4. 检查 POST Body 攻击特征。
5. 执行用户规则、config.ly 规则和系统规则。
6. Observe 记录命中并继续。
7. Reject、Block、Captcha 等执行动作并短路。

Args/Post 攻击会同时记录 CC Attack Event，为高频攻击规则提供输入。

## 捕获流的意义

拦截和 Observe 事件需要关联 Header、Body 与响应。CaptureStream 在透传的同时保留受限副本，避免日志组件再次消费不可重复读取的请求流。

## 异常处理

`BadHttpRequestException` 返回 413，其他检测异常当前返回 400。异常信息直接写响应时要注意生产环境不要泄露内部细节。

## 为什么在 Captcha 前

明确攻击应先由 WAF 记录和处置；Captcha 处理的是已经被标记为待验证的来源。反过来会让攻击请求只看到验证码，却缺少准确 WAF 命中事件。

## 调优与排障

- 新规则先 Observe，完成合法流量评估后再阻断。
- Body 规则不生效：检查 Content-Type、请求大小和 Body 是否可读。
- 误报：缩小 Field、Path、Method 或 Operator，而不是全局关闭。
- 请求已被前层 AccessControl 拒绝时不会进入 WAF。

## 规则发布流程

新规则先在 Observe 模式采集命中样本，按域名、路径、方法和字段缩小范围，再切换 Reject 或 Block。规则变更应记录操作者和版本，发生误报时能够快速回退。

## 验证方法

为 Query、Header、JSON、表单和超大 Body 分别准备正常与攻击样本。确认命中规则 ID、动作、状态码和审计事件一致；对编码、重复参数和不同 Content-Type 做边界测试，并观察 CPU 与延迟。
