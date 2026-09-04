# 自动 HTTPS 与证书生命周期

LyWaf 的 HTTPS 证书来源包括配置文件证书、ACME 公网证书和显式启用的内部 CA。TLS 握手先使用已加载的精确或通配证书；缺失时才评估按需签发策略，最后保留原有默认证书回退行为。

## 固定域名与多 CA 回退

```ly
Acme {
    Enabled = true
    Email = "admin@example.com"
    AcceptTermsOfService = true
    Domains = ["example.com", "www.example.com"]
    DirectoryUrls = [
        "https://acme-v02.api.letsencrypt.org/directory",
        "https://acme.zerossl.com/v2/DV90"
    ]
    AccountKeyPath = "certs/account.pem"
    CertificatePath = "certs"
}
```

CA 按列表顺序初始化和签发；单个 CA 初始化或订单失败后继续下一项。每个 CA 使用独立账户密钥，首项为 `account.pem`，后续依次追加 `.1`、`.2`。账户摘要可在证书管理页或 API 查看，但私钥内容永不返回。

HTTP-01 要求域名解析到 LyWaf，公网可访问挑战 HTTP 端口。通配符证书需要 DNS-01，当前实现没有提供 DNS Provider，因此不要把通配符名称直接放入 `Domains` 请求 HTTP-01。

## 按需签发

```ly
OnDemandEnabled = true
OnDemandAllowedDomains = ["api.example.com", "*.tenant.example.com"]
OnDemandMaxRequestsPerHour = 10
OnDemandTimeoutSeconds = 20
```

按需签发默认关闭。TLS SNI 必须匹配固定 `Domains` 或允许列表，任意公网名称不能触发请求。相同域名的并发握手共享一个任务，每小时配额的检查与任务登记在同一临界区内。超时只终止当前握手等待；签发任务可以完成并供下一连接使用。

允许列表仍应尽量窄。它不是域名所有权验证的替代品，HTTP-01 仍由 CA 验证。

## 内部 CA

```ly
InternalCaEnabled = true
InternalCaPath = "certs/internal-ca"
InternalCertificateDays = 30
Domains = ["localhost", "service.dev.example"]
```

内部 CA 用于本地和受控内网。首次运行生成持久化根证书与 3072 位 RSA 私钥，叶证书使用独立 2048 位密钥并包含 DNS SAN。Unix 私钥权限设置为 `0600`。客户端必须通过受控方式信任 `root-ca.pem`；禁止把根私钥复制到客户端或提交版本控制。

公网 ACME 与内部 CA 可独立启用。若两者同时启用，公网 CA 全部失败后才回退内部 CA，因此生产环境通常不应开启内部回退。

## 多节点协调

```ly
CoordinationLockPath = "certs/locks"
CoordinationLockTimeoutSeconds = 30
```

同一域名使用 SHA-256 命名的排他锁文件。等待者取得锁后重新读取共享证书，避免重复订单。多节点必须让证书目录、账户目录和锁目录位于支持排他文件锁与一致可见性的共享存储；普通本地目录只能协调同机进程。不能确认网络文件系统锁语义时，应只允许一个签发节点并分发只读证书。

## 状态、吊销与指标

`CheckRevocationOnline` 使用操作系统 `X509Chain` 执行在线吊销检查，网络超时由 `RevocationCheckTimeoutSeconds` 控制。底层可能使用证书提供的 OCSP 或 CRL 地址。结果为 `valid`、`revoked`、`unknown` 或 `error`；临时状态服务故障不会立即卸载证书。

这项能力是服务端状态检查，不等同于 TLS OCSP Stapling。当前 LyWaf 不承诺向客户端装订 OCSP 响应。

- `lywaf_certificate_expiry_timestamp_seconds{domain,source}`：证书到期 Unix 时间。
- `lywaf_certificate_status{domain,status}`：有效为 1，未知/错误为 0，吊销/过期为 -1。
- `GET /api/v1/settings/certs/status`：证书来源、剩余天数、检查时间与 CA 账户摘要。

建议在剩余 30、14、7 天分级告警，并对 `revoked` 立即告警。`unknown` 需要结合出口网络和 CA 状态判断，不能自动等同于吊销。

## 启动校验与更新边界

启用公网 ACME 时必须同意条款并填写有效邮箱；开启按需签发必须存在允许范围；URL、配额、超时、续期窗口和目录在 Host 启动前校验。证书申请成功后同一进程的新 TLS 握手立即使用新证书；监听端口和 HTTPS 启用状态仍属于重启边界。
