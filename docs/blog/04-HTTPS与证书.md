# 为站点启用 HTTPS 与证书自动化

> 专栏第 4 篇。目标是在不改变上游服务的情况下，由 LyWaf 统一终止 TLS。

## TLS 放在哪一层

客户端与 LyWaf 使用 HTTPS，LyWaf 到同机上游可使用 HTTP；跨主机或不可信网络中的上游仍建议 HTTPS。入口集中终止 TLS 能统一证书、协议和重定向策略。

## 手工 PEM 证书

```ly
http_port = 80
https_port = 443

Certs {
    example.com {
        PemFile = "certs/example.com.pem"
        KeyFile = "certs/example.com.key"
    }
}

example.com {
    proxy = ["http://127.0.0.1:8080"]
}
```

`Certs` 中的域名用于 SNI 选择。证书必须覆盖该域名，私钥与证书必须配对。

## 本地验证

DNS 尚未切换时：

```powershell
curl.exe -k -i --resolve example.com:443:127.0.0.1 https://example.com/
```

`-k` 只适合测试自签名或尚未建立信任的证书，生产验收应移除它。

## ACME 自动证书

仓库的 `config.ly.example` 提供 `Acme` 配置。HTTP-01 的关键条件是：

- 域名 DNS 已指向入口。
- 公网 80 端口能到达 LyWaf。
- 外层代理不会拦截 `/.well-known/acme-challenge/`。
- 证书和账号数据目录可写且会持久化。

正式签发前先使用 ACME staging，避免反复调试触发 CA 限额。

## HTTP 到 HTTPS

开启自动 HTTPS 前先确认 HTTP 正常，并保证 ACME Challenge 中间件优先于重定向。API 客户端、Webhook 和健康检查可能不自动跟随跳转，应在切换前验证。

## 私钥安全

- 私钥只允许运行账号读取。
- 不提交 Git，不写进日志和截图。
- 备份应加密并限制访问。
- 更换证书时同时验证 SNI、多域名和回滚文件。

## 常见问题

| 现象 | 原因 |
|---|---|
| 浏览器提示域名不匹配 | SNI 域名与证书 SAN 不一致 |
| HTTPS 端口启动失败 | 文件路径、权限或私钥格式错误 |
| ACME 一直失败 | 80 端口或 DNS 尚未到达当前实例 |
| 重定向循环 | 外层代理协议头与 LyWaf 判断不一致 |

## 证书验收

从外部网络检查证书链、SAN、到期时间和 TLS 握手，从本机检查私钥权限与 SNI。请求 HTTP 业务路径应跳转到正确 HTTPS 地址，而 ACME Challenge 仍能通过 80 端口直接返回。配置证书到期告警并实际记录续期日志。

## 回滚准备

保留上一张有效证书和绑定配置。新证书加载失败时恢复文件与配置后重启；不要在证书故障中同时修改 DNS、监听和上游。

## 本篇小结

入口已经具备 HTTPS。下一篇会把单上游扩展为多上游，并让健康检查和负载均衡真正参与流量分配。
