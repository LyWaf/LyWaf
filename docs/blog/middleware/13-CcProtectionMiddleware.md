# CcProtectionMiddleware：CC 请求侧处置

## 位置

位于 Captcha 后、带宽与请求速率限制之前。只有通过访问控制、WAF 和当前验证码状态的请求才进入 CC 检查。

## 检查输入

中间件获取真实客户端 IP，将 HttpContext 交给 `ICcRuleChecker.CheckRequest`。规则可基于高频访问、高频攻击和复合条件判断。

高频攻击数据部分来自更前面的 WafControl；高频错误数据来自更后面的 CcResponseMonitor。

## 动作分支

- 未触发：调用下游。
- `LogOnly`：执行记录动作，但继续调用下游。
- `Block`：封禁并返回，短路。
- `Captcha`：标记待验证并返回，后续请求由 CaptchaMiddleware 接管。
- `Reject`：返回拒绝状态，短路。
- `RateLimit`：执行规则定义的限速处置。

## 为什么 LogOnly 仍执行 Action

ExecuteAction 不只代表拒绝，也负责记录规则触发信息。LogOnly 需要留下事件，但不能改变业务结果。

## 与 SpeedLimit 的区别

CC 是按时间窗口和行为特征判断异常；SpeedLimit 是路由绑定的明确请求配额。一个用于检测行为，一个用于执行固定容量控制。

## 排障

- 规则无命中：检查统计窗口、真实 IP 和 Target 字段。
- 所有 NAT 用户一起触发：阈值过低或真实 IP 链错误。
- Captcha 动作没有页面：本次请求可能已返回，下一次请求才由 Captcha 拦截。
- LogOnly 仍有事件属于正常行为。

## 阈值设计

阈值应基于真实业务基线，区分静态资源、API、登录和长轮询。NAT、公司出口和搜索引擎会让单 IP 请求量天然较高，不能只用一个全局阈值。自动封禁要有最大时长和人工解除入口。

## 验证方法

以受控速率逐步增加请求，确认窗口边界、动作切换和恢复时间。分别测试 LogOnly、Reject、Captcha 和 Block，并检查事件只记录一次合理处置，不产生递归封禁。
