# 首轮完整回归结果

## 漏拦修复复验（2026-09-14，严格模式）

针对禁用自动拉黑后剩余的 116 个漏过探针，修复数字 SQL 布尔条件、报错函数、命令替换词法解析及换行命令、整数/十六进制/缩写回环 URL、模板算术、Cookie 中 PHP 序列化对象及 XML 换行归一化后的头注入。请求候选数量耗尽时显式产生检查异常，避免静默跳过后续参数；仍保留容量限制。

执行 `node tests/hurl-security/run.mjs strict --grouped`：1430 个探针、4290 次请求，1009 个攻击全部返回 403 且未到达上游，421 个正常探针中 412 通过、9 个原有误拦，所有前后对照通过，无缺失响应。完整探针通过数 1421；Hurl 仍返回退出码 4，未修改测试断言。

报告目录：`artifacts/hurl-security/2026-09-14T04-21-22.089Z-strict/`。本结果只适用于当前样本矩阵和严格模式，不能表示所有攻击均可识别。语义检测的生产默认 Observe 未修改；要拒绝命中请求需配置 `Protect.SemanticDetectionAction = "Reject"`。自动拉黑只在隔离运行器中禁用。

剩余误拦：`benign-02`、`benign-sql`、`benign-directory` 的 query/query-double/query-duplicate 组合，共 9 个。

## XSS 修复复验（2026-09-14）

以下为后续修复结果；下文首轮数据保留作为历史基线。

- 新增 `sys-xss` 系统拒绝规则，不限 GET/POST，覆盖请求体、查询参数及所有请求头（含 Cookie），补充事件属性和 javascript URI 特征。
- 标准 zlib 封装的 HTTP deflate 正确解码，保留旧 raw-deflate 支持及解压容量限制。
- 实际执行 `node tests/hurl-security/run.mjs default --grouped`，内部直接调用 Hurl 执行分类文件。`xss.hurl` 的 90/90 攻击探针均返回 403 且未到达测试上游，前后正常对照均到达上游并返回 200。未修改攻击或正常请求的断言。
- .NET 测试 594/594 通过，包含新增的跨方法/字段 XSS 与标准 deflate 测试。
- 全分类仍未通过：1430 个探针中 668 通过，1009 个攻击中 753 到达上游，421 个正常探针中 9 个误拦；无缺失响应或正常对照失败。其他类别尚未修复，Hurl 仍返回退出码 4。修复 deflate 后不再因解码错误拒绝所有此类请求，因此旧拦截数不能直接视为检测能力。

完整报告：`artifacts/hurl-security/2026-09-14T02-57-38.870Z-default/`。仅证明当前 XSS 测试矩阵通过，不代表覆盖所有 XSS 变体。

执行日期：2026-09-14。环境：Windows、.NET 9、Hurl 7.1.0。

本轮只新增测试与报告，没有修改检测规则或把失败用例改为允许。结果不是“全部正常”。

分类文件复验：`cases/` 的 21 个 `.hurl` 文件已由 Hurl 7.1.0 实际执行，共 4290 次请求，1430 个探针均得到响应，前后对照全部正常。严格模式仍为拦截 863、漏过 146、正常请求误拦 37，与下表一致。分类版报告位于 `artifacts/hurl-security/2026-09-14T02-13-00.866Z-strict/`。多个探针合并到一个文件后，Hurl 的“成功文件数”不再等于成功探针数；应查看逐请求断言和 REPORT.md。

## 最终结果

| 指标 | default | strict |
|---|---:|---:|
| 独立用例 | 1430 | 1430 |
| 实际 HTTP 请求（含前后对照） | 4290 | 4290 |
| 攻击探针 | 1009 | 1009 |
| 返回 403 且未到上游 | 269 | 863 |
| 攻击到达上游（实际 200） | 740 | 146 |
| 正常探针 | 421 | 421 |
| 正常探针误拦截 | 37 | 37 |
| 完整 Hurl 文件断言通过 | 653 | 1247 |
| 缺失响应 | 0 | 0 |
| 前后正常对照失败 | 0 | 0 |

两轮合计 8580 次 HTTP 请求。Release 构建零警告、零错误，生成器自测通过。Hurl 正确返回失败退出码，完整保留了失败断言。

default 的语义检测为 Observe，所以发现攻击不等于主动拒绝；strict 只把语义检测改为 Reject，并不是一套经过调优的全新规则。默认 regex 规则文件已复制到隔离环境。测试 IP 的后台流量分析阈值提高，避免封禁干扰。

上述 403 仅证明请求被拒绝，并不证明分类正确：例如当前 deflate 解码问题会同时拦截正常和恶意压缩体。统计必须结合误报一起阅读，不能仅看拦截总数。

## 严格模式仍存在的问题

| 问题 | 可复现用例 | 结果 |
|---|---|---|
| XSS 在部分请求位置未统一检查 | extra-xss-script--header / cookie / json-put / json-patch | 200，到达上游 |
| 部分 XSS 事件和伪协议特征未覆盖 | extra-xss-event、extra-xss-link 的部分变体 | 200，到达上游 |
| 简单布尔 SQL 和错误函数特征不足 | extra-safeline-kong、extra-sql-error 的部分变体 | 200，到达上游 |
| 命令替换、换行命令漏检 | extra-cmd-substitution、extra-cmd-newline 的部分变体 | 200，到达上游 |
| 非点分形式的本机地址未识别 | extra-ssrf-integer、extra-ssrf-hex 的非 deflate 变体 | 200，到达上游 |
| 模板规则依赖特定算术样例 | extra-ssti-other 的非 deflate 变体 | 200，到达上游 |
| CRLF 内容未判为危险 | extra-crlf 的非 deflate 变体 | 200，到达上游；是否可利用取决于业务使用方式 |
| 检查候选上限之后的参数未检出 | edge-candidate-limit--raw | 200，到达上游 |
| PHP 序列化特征在 Cookie 中漏检 | php-01--cookie | 200，到达上游 |

这份表列出代表用例；每个失败 ID、状态码和上游记录都在运行目录的 REPORT.md/origin-results.json 中，不以本表替代完整结果。

## 误拦截

1. 28 个正常 deflate 请求全部返回 403。生成器使用 Node `deflateSync` 产生标准 zlib 封装流，而当前 `RequestInspectionService.DecodeContentEncoding` 使用 `DeflateStream`。应进一步修正和验证解码兼容性，不能把这些正常请求的预期改成 403。
2. 其余 9 个误拦截来自 `benign-02`、`benign-sql`、`benign-directory` 的三种查询传输形式。默认规则文件启用后出现，应定位过宽的 Args 正则并增加对应反例回归。

优先建议修复正常压缩请求、请求位置覆盖和检查预算耗尽策略，再完善 SQL/XSS/命令/SSRF 的识别，修复后重跑同一矩阵。不能简单取消资源上限，否则会引入内存或 CPU 风险。

## 原始报告

- 严格模式：`artifacts/hurl-security/2026-09-14T01-59-43.285Z-strict/REPORT.md`
- 默认模式：`artifacts/hurl-security/2026-09-14T02-00-33.065Z-default/REPORT.md`
- 同目录有 HTML、JUnit、Hurl JSON Lines、全部 .hurl 文件和上游接收记录。

原始 artifacts 被 Git 忽略；本文保留本次摘要，重新运行会产生新的独立目录。早期调试轮次存在分析封禁干扰或未复制规则文件，不纳入本报告。

## 边界

这是合成内容探针的本地端到端回归，不是漏洞利用成功率或行业基准。没有在雷池实例上测试，未完整获取其外部语料，不应声称覆盖雷池的全部测试或做产品准确率排名。来源和未覆盖的协议/业务场景见 README.md。
