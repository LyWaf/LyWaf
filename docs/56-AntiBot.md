# Anti-Bot 风险分析与挑战

Anti-Bot 将客户端分为浏览器、API 客户端、搜索爬虫、扫描器、自动化工具和未知客户端。分析结果包含 0-100 风险分、稳定的 16 字符 TLS/HTTP 指纹以及可解释原因列表，并保存到当前请求的 `AntiBot.Assessment` 和结构化日志。

## 指纹边界

当前指纹使用 HTTP 协议、TLS 版本、协商密码套件、User-Agent、Accept、Accept-Language 和 Accept-Encoding 的 SHA-256 摘要。它是稳定的低基数风险特征，适合请求关联。

Kestrel 的公开握手特征不包含生成 JA4 所需的完整 ClientHello 扩展及顺序。TLS 在支持 JA4 的负载均衡器、Envoy 或捕获层终止时，可通过 `Ja4Header`（默认 `X-LyWaf-JA4`）传入 JA4。只有连接源 IP 在 `TrustedFingerprintProxies` 中时才采信，并校验 JA4 格式；公网客户端伪造同名 Header 会被忽略。LyWaf 不把应用层近似摘要误称为原生 JA4。

## 客户端分类

已知扫描器和无头自动化特征会提高风险；浏览器缺少 Accept-Language 或 Fetch Metadata 也会计分。curl、Postman、移动 SDK 等 API 客户端会单独分类，不会因为不是浏览器就自动拦截。搜索爬虫 UA 标记为“未验证”，当前不会通过反向 DNS 把声明 Googlebot 的请求直接加入白名单。

## JavaScript 挑战

`Challenge` 模式向高风险浏览器返回一个无缓存页面。页面使用 WebCrypto 计算 SHA-256 工作量证明，再向 `ChallengePath` 提交结果。挑战令牌由独立密钥进行 HMAC-SHA256 签名，并绑定域名、客户端 IP、User-Agent、原始相对路径和过期时间。验证成功后服务端写入 HttpOnly、SameSite=Lax Cookie；客户端不能自行伪造会话。

`Secret` 必须使用与控制台 JWT、分布式节点和其他插件完全不同的高熵值。未配置时进程会使用临时随机密钥，重启后已有挑战会话全部失效，多节点也无法共享验证状态。

## 模式与恢复

- `Observe` 只记录风险，不改变响应。
- `Challenge` 仅对达到 `ChallengeScore` 的请求执行浏览器挑战。
- `Block` 对达到 `BlockScore` 的请求返回 403。

误判时可通过 `BypassPaths` 或精确 `BypassIps` 恢复访问，并根据日志中的原因降低风险或调整阈值。健康检查、监控和纯 API 路径应显式旁路或保持观察模式。挑战难度范围 1-6，过高会显著增加低性能终端耗时。

## 后台管理

- `GET /api/anti-bot` 读取当前配置；只返回 `secretConfigured`，不会返回会话密钥。
- `PUT /api/anti-bot` 更新模式、阈值、挑战参数、旁路和可信 JA4 代理。

更新由 `AntiBotEndpoints` 校验，再经版本化配置补丁原子写入和热重载。新密钥至少 32 个字符；留空表示保留原密钥。
