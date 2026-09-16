# Hurl 攻击回归测试

## 直接使用 Hurl

`cases/` 已包含可直接运行、纳入版本控制的分类文件，不需要 Node.js，也不需要先生成。

仅测试 XSS（把地址替换为自己授权的测试 WAF 地址）：

```powershell
hurl --test --continue-on-error --path-as-is --noproxy "*" --variable base_url=http://127.0.0.1:5002 tests/hurl-security/cases/xss.hurl
```

运行全部分类：

```powershell
hurl --test --jobs 1 --continue-on-error --path-as-is --noproxy "*" --variable base_url=http://127.0.0.1:5002 --glob "tests/hurl-security/cases/*.hurl"
```

`base_url` 不带末尾斜线，指向 WAF 的业务监听端口，而非管理端口。测试上游的 `/` 和 `/probe` 应返回 200；敏感路径用例按原始路径发送。每个探针前后访问 `/` 并断言 200，避免全局封禁导致虚假的拦截结果。测试时需要关闭会干扰结果的 CC/自动封禁，或提高对应阈值。如果正常对照失败，不能据此计算内容拦截率。请使用不执行载荷的测试上游，不要运行于生产。

文件包括 `xss.hurl`、`sqli.hurl`、`command.hurl`、`code.hurl`、`ssrf.hurl`、`xxe.hurl`、`traversal.hurl`、`lfi.hurl`、`rfi.hurl`、`nosql.hurl`、`ldap.hurl`、`xpath.hurl`、`template.hurl`、三类 `*-deserialization.hurl`、`crlf.hurl`、`sensitive-path.hurl`、`protocol.hurl`、`boundary.hurl`、`benign.hurl`，共 21 个分类文件、1430 个探针。

普通文本/JSON/XML 请求体可直接阅读；压缩内容、需要精确字节的请求及代码注入样本保留合法的 Hurl `base64,...;` 语法。代码注入样本保留编码是为了避免本地杀毒软件将测试文件隔离；发送时仍是原始载荷，不会改变线上检测方式。

分类文件不要求专用上游响应头或固定响应体，直接断言攻击 403、正常 200。不能把 502、断连或未运行的请求视为拦截成功。需要严格核对请求是否到达上游时，可使用下面的可选隔离运行器。

## 可选隔离环境

### localhost 的正常对照也返回 403

先运行 `curl.exe --noproxy "*" -i http://localhost/`。如果无攻击参数也返回 403，且页面提示 IP 异常，说明测试受到 IP 封禁等策略干扰，并不是 Hurl 格式错误。2026-09-14 本机排查就观察到了这一现象。

在专用测试环境解除已有封禁或等待过期，并调整 CC/自动封禁阈值，确保 `/` 和 `/probe` 正常返回 200 后再执行。不要把测试 IP 加入绕过 WAF 的白名单，也不要把正常对照改成预期 403。升级本次 XSS 修复后需重新启动实际受测的 LyWaf 进程；仅编译不会更新正在运行的进程。

在仓库根目录运行，需要 .NET 9 SDK、Node.js 和 Hurl（本次使用 7.1.0）：

```powershell
node tests/hurl-security/run.mjs default
node tests/hurl-security/run.mjs strict --grouped
node --test tests/hurl-security/generate.test.mjs
```

发现漏拦截、误拦截或断言失败时，命令返回非零退出码。这是测试发现问题，不应通过修改预期状态码让测试变绿。

脚本自动编译 Release，启动两个仅监听 127.0.0.1 的隔离服务：真正的 LyWaf 和固定返回 `origin-ok` 的测试上游。端口动态分配，配置、数据库、日志、补丁均位于本次独立的 `artifacts/hurl-security/<时间>-<模式>/` 中，不加载用户的 config.ly 或已有运行数据，结束后关闭自己启动的服务。不要同时执行两个构建任务。

上游只消费请求并返回固定文本，不执行 SQL/命令，不读取请求指定的文件，不渲染 HTML，也不访问载荷内的 URL。不要把测试改为攻击公网或生产站点。

## 两种策略

| 模式 | 策略 |
|---|---|
| default | 使用代码默认 ProtectOptions、系统 WAF 规则和仓库 Rules 文件。语义检测默认 Observe，并非拒绝模式 |
| strict | 在 default 基础上，仅设置 SemanticDetectionEnabled=true、SemanticDetectionAction=Reject |

两者都复制仓库 Rules 目录，不导入用户自定义规则。健康检查关闭；通过 `Plugins.DisabledPlugins = ["analysis"]` 在隔离配置中禁用自动拉黑的流量分析插件，不再依赖提高触发阈值。全新隔离目录不加载已有 IP 黑名单或自定义 CC 规则，WAF 内容规则和语义检测仍正常工作。结束后关闭测试进程，不修改生产配置，也不需要手动恢复。因此这里的 default 指默认内容检查策略，不代表完整生产部署或用户现有配置。CC/自动封禁功能本身应在另一个启用它们的测试环境验证。

## 覆盖范围

分类文件内共有 1430 个探针：1009 个攻击探针、421 个正常请求；每个探针另有前后两个正常请求，共 4290 次 HTTP 请求/模式。旧版逐用例生成器保留为可选维护工具。

- SQL、命令、代码注入，XSS、SSRF、XXE、路径穿越、敏感路径。
- Java/PHP/.NET 反序列化特征、NoSQL、LDAP、XPath、模板/表达式注入、CRLF。
- 查询参数、重复参数、二次编码、请求头、Cookie、表单、嵌套 JSON、PUT/PATCH、纯文本、XML、multipart。
- gzip、标准 zlib 封装的 deflate、Brotli；非法压缩数据和未知 Content-Encoding。
- GET 请求体、DELETE/OPTIONS、JSON Unicode、参数名注入、三次编码、候选数量和长文本检查边界。
- 正常英文、中文、数学表达式、标点、URL、邮箱、JSON、XML、大文本，以及容易误报的词语。

矩阵来自 94 个基础样本的 15 种传输组合，外加 20 个独立路径/边界探针。组合用例不是同等数量的独立漏洞。语料中的危险字符串表示需要检查的特征，不能单凭通过 WAF 就认定真实业务存在可利用漏洞。

这不是雷池“全部测试资源”的复制，也未实际运行雷池，因此不能用本报告计算两款产品的相对拦截率。HTTP/2 二进制协议异常、请求走私、WebSocket 帧、CC/爆破、人机验证和真实应用利用链不包含在此内容检查矩阵内，需要独立夹具和策略。

## 判定与报告

攻击要求精确返回 403 且没有上游标记；正常请求要求 200、上游标记及固定响应体。502、连接失败、重定向都不能算成功拦截。Hurl 禁止代理环境变量影响回环请求，不跟随重定向，按原始路径发送，串行执行、不重试。

每个用例的前后对照请求必须正常，防止“封掉整个 IP”导致看起来拦截率很高。测试上游另外记录用例 ID，核对攻击是否真的到达上游。

输出目录包含：

- `cases/*.hurl` 和 `manifest.json`：可直接检查和复现的请求与分类。
- `REPORT.md`、`summary.json`：分类统计和全部失败用例。
- `origin-results.json`：每个用例的实际状态码、Hurl 断言结果、上游接收记录。
- `hurl.json`：Hurl 的 JSON Lines 原始结果；`hurl.log`：失败断言详情。
- `junit.xml`、`html/index.html`：CI 和可视化报告。
- `config.yaml`、`Rules/`、`server.log`：配置与日志。

只有 missingResults=0、failedControls=0 时才应讨论内容检测率。报告可能包含完整攻击请求，仅使用合成数据，不要加入真实凭据或客户数据。

维护用生成命令：`node tests/hurl-security/generate.mjs --grouped` 更新分类文件；不带 `--grouped` 则生成旧版独立文件到被 Git 忽略的 generated 目录。日常测试直接运行上面的 Hurl 命令即可。

## 参考来源

查阅时间：2026-09-14。

1. [SafeLine README](https://github.com/chaitin/SafeLine)：公开的攻击分类，用来建立覆盖清单，不照搬其性能/准确率结论。
2. [SafeLine Kong 示例](https://github.com/chaitin/SafeLine/blob/main/sdk/kong/Readme.md)：简单布尔 SQL 探针，标记 extra-safeline-kong。
3. [SafeLine issue #1222](https://github.com/chaitin/SafeLine/issues/1222)：社区报告的 Content-Encoding 场景，标记 extra-safeline-gzip，并扩展为正常与异常压缩体测试；不视为当前版本缺陷的已验证结论。
4. [SafeLine 社区讨论 #296](https://github.com/chaitin/SafeLine/discussions/296)：链接公开评测资源，但本轮未完整获取和导入其外部数据集。
5. 仓库 `tests/fixtures/security-corpus-v1.json`：原有合成语料，经真实 HTTP 传输验证，而非只调用检测器。
6. [Hurl 请求格式](https://hurl.dev/docs/request.html)：使用 Base64 body 精确发送字节，尤其是压缩体和模板字符串。Hurl 的 Base64 语法会在发送前解码，并不是在 HTTP 中增加一层 Base64。

所有新增扩展样本均为本项目合成探针；未批量复制第三方代码或未知授权的数据集。
