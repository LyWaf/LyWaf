# ResponseCompressMiddleware：响应压缩

## 位置

位于 CORS 之后、高优先级插件之前。请求向内执行时它先安装响应缓冲；响应向外返回时，它看到插件和上游已经生成的最终内容。

## 算法选择

根据 `Accept-Encoding` 和配置选择：

1. Brotli，客户端包含 `br` 且已启用。
2. Gzip，客户端包含 `gzip` 且已启用。
3. 都不支持则跳过压缩。

还会检查 HTTPS 压缩开关、最小大小、Content-Type、状态码和已有 Content-Encoding。

## 响应流程

1. 保存原始 Response Body。
2. 将下游响应写入 MemoryStream。
3. 下游完成后判断是否应该压缩。
4. 压缩到第二个 MemoryStream。
5. 只有压缩结果更小时才采用。
6. 移除旧 Content-Length，设置 Content-Encoding、新长度和 `Vary: Accept-Encoding`。
7. 写回原始响应流。

## 短路与异常

未启用、客户端不支持或 HTTPS 配置禁止时直接继续。压缩失败会恢复原始流，并尽量回写未压缩内容。

## 顺序原因

WebTamper 位于它的内层。响应回程时防篡改先决定当前内容或安全基线，压缩中间件再压缩真正发送的版本。反过来会对压缩字节做内容基线，导致编码差异干扰 Hash。

## 内存风险

当前实现会完整缓冲原始和压缩结果，大响应峰值可能超过内容大小的两倍。对视频、大下载或流式接口应排除对应 MIME、关闭动态压缩或使用文件服务预压缩。

## 排障

- 没有 Content-Encoding：检查 Accept-Encoding、MIME、MinSize 和 HTTPS 开关。
- 压缩后反而变大：实现会自动回退原始内容。
- 流式接口延迟：完整缓冲会等下游完成，应避免用于真正流式响应。

## 缓存与内容协商

压缩表示不同于原始表示，响应必须带 `Vary: Accept-Encoding`。已有 ETag 若基于未压缩内容，需要确认缓存语义；对 Range、SSE、WebSocket 和已压缩静态文件应直接旁路。

## 验证方法

用 `curl --compressed` 和不带 Accept-Encoding 的请求对比 Header 与解压后内容。测试小于阈值、不可压缩 MIME、Brotli、Gzip、204、304 和大文件，观察延迟与峰值内存。
