# FileProviderMiddleware：静态文件服务

## 位置

位于 SimpleRes 后、LoadBalancing/YARP 前。命中文件路由时由 LyWaf 本地返回文件，不访问上游。

## 路由识别

读取 RouteModel.RouteId，仅处理 `fileserver_` 前缀，再从 `FileServerOptions.Items` 获取 Prefix、根目录、首页、Browse、TryFiles 和预压缩设置。

## 路径解析

根据请求 Path 与 Prefix 计算相对路径，交给 FileService 解析真实文件。找不到时使用统一 404。路径解析必须保证结果停留在配置根目录，防止 `../` 穿越。

## 目录流程

- Prefix 本身缺少尾斜杠：重定向到 `/` 结尾。
- 实际目录缺少尾斜杠：重定向。
- Browse=false：返回 403。
- Browse=true：枚举目录和文件生成 Index 页面。

生产环境通常关闭 Browse，因为目录列表会暴露文件名、大小和时间。

## 缓存协商

读取文件元数据后处理：

- `If-None-Match` 与 ETag 一致：304。
- `If-Modified-Since` 不早于文件修改时间：304。
- 否则继续读取内容。

## Range 与预压缩

解析 Range Header 后由 FileService 返回 206 或普通响应，并设置 Content-Type、Content-Length、Last-Modified、ETag 与 Accept-Ranges。命中预压缩文件时设置 Content-Encoding 和 `Vary: Accept-Encoding`。

## 流与错误

文件使用 Stream.CopyToAsync 写入响应，结束后异步释放。读取失败尝试返回 500；如果响应已经开始，客户端可能只收到截断内容，日志必须保留异常。

## 排障

- 404：检查 Prefix、Root、TryFiles 和发布目录。
- 403：请求命中目录但 Browse 关闭，或文件权限不足。
- Range 不工作：检查上游 CDN 是否移除 Header。
- 文件暴露过多：确认 Root 没有指向程序、证书或 datas。

## 生产安全

根目录使用规范化绝对路径，并拒绝符号链接或重解析点逃逸策略不明确的情况。敏感扩展名、隐藏文件和目录列表应默认拒绝。下载响应加入合适的缓存、MIME 和 `X-Content-Type-Options`。

## 验证方法

测试首页、尾斜杠、目录浏览开关、TryFiles、404、ETag/304、Range/206、预压缩和大文件取消。专门尝试 URL 编码的 `..`、混合分隔符和符号链接，确认无法越过 Root。
