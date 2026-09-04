# SimpleResMiddleware：固定与 Mock 响应

## 位置

位于普通/低优先级插件之后、FileProvider 和 YARP 之前。它是本地终止请求的处理器。

## 匹配方式

从当前 Endpoint 获取 RouteModel.RouteId。只有以 `simpleres_` 开头，并且在 `SimpleResOptions.Items` 中找到对应项时才处理；其他请求继续。

## 响应流程

1. 设置配置的状态码。
2. 生成包含 Charset 的 Content-Type。
3. 写入额外响应 Header。
4. 替换 Body 中的占位符。
5. 可选追加请求 Header，供调试使用。
6. 按配置编码写入 Body。
7. 直接返回，不访问文件服务和上游。

## 支持的占位符

包括 Port、Host、Path、Method、Query、Scheme、ClientIp、Time、Date、URL、UserAgent 和 RouteId 等。替换器只处理合法花括号名称，避免任意模板表达式执行。

## 配置热更新

中间件订阅 `IOptionsMonitor<SimpleResOptions>.OnChange`，更新内存中的 Items，并记录新数量。路由结构本身是否热更新仍取决于配置提供器和 YARP 路由更新。

## 适用场景

- `/health` 健康响应。
- 维护页面或临时降级响应。
- Mock API 和调试回显。
- 无需上游的状态码测试。

## 安全注意

`ShowReq` 会回显请求 Header，可能泄露 Authorization/Cookie，只用于受控测试。模板内容和 Header 值也应防止注入敏感信息。

## 排障

- 配置存在但进入上游：检查 RouteId 是否为 simpleres 前缀。
- 占位符未替换：检查名称是否受支持和花括号是否合法。
- 修改未生效：区分 Item 内容变更与路由结构变更。

## 响应完整性

自定义 Header 需要过滤换行，Body 编码与声明 Charset 必须一致。HEAD 请求应返回与 GET 一致的元数据但不写正文。模板变量来自请求时，应避免把敏感 Header 或未经处理的内容注入 HTML。

## 验证方法

覆盖自定义状态码、Header、中文编码、占位符、HEAD 和配置热更新。确认命中时上游没有收到请求，未命中时正常进入 FileProvider 或 YARP。
