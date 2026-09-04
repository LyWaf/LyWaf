# LyWaf - 临源Web应用防火墙

> 安全近在咫尺，攻击远在天涯

LyWaf 是一款基于 .NET 9 和 YARP（Yet Another Reverse Proxy）构建的高性能 Web 应用防火墙（WAF），集成了反向代理、负载均衡、安全防护、流量控制等功能于一体。

LyWaf 聚焦 Web 安全网关，不以复制 Nginx、Caddy、Envoy 或 Traefik 的全部生态为目标；详细取舍见 [产品定位与能力边界](docs/58-产品定位与能力边界.md)。性能结论必须来自同条件、可复核的基准数据。

## ✨ 特性

- 🚀 **高性能反向代理** - 基于 YARP 构建，支持 HTTP/HTTPS 代理
- ⚖️ **多种负载均衡策略** - 支持 11 种负载均衡算法
- 🛡️ **WAF 安全防护** - 内置 SQL 注入、XSS 等攻击检测
- 🔒 **IP 访问控制** - 支持黑白名单，CIDR 网段匹配
- 🌍 **地理位置限制** - 基于 IP2Region 的国家/地区访问控制
- 🚦 **流量控制** - 请求限速、连接限制、带宽控制
- 📦 **响应压缩** - Gzip/Brotli 压缩，按大小和 MIME 类型智能压缩
- 🔑 **自动证书** - Let's Encrypt ACME 协议，自动申请和续期 HTTPS 证书
- 💚 **健康检查** - 主动健康检查，自动剔除故障节点
- 📁 **静态文件服务** - 内置文件服务器功能
- 📊 **统计分析** - 访问统计、CC 攻击检测
- 🔐 **HTTPS 支持** - SNI 多证书、自动 HTTPS 重定向
- 📝 **简洁配置** - 类 Caddy 的 .ly 配置格式，支持变量和条件逻辑

## 📦 安装

### 环境要求

- .NET 9.0 SDK 或更高版本

### 编译

```bash
git clone https://github.com/LyWaf/LyWaf.git
cd LyWaf
dotnet build -c Release
```

## 🚀 快速开始

### 命令行模式

LyWaf 支持多种运行模式：

#### 1. 反向代理模式

```bash
# 简单代理
LyWaf proxy -f 0.0.0.0:80 -t http://backend:8080

# 带 HTTPS
LyWaf proxy -f https://0.0.0.0:443 -t http://backend:8080 --cert-pem cert.pem --cert-key cert.key

# 添加自定义 Header
LyWaf proxy -f :80 -t http://backend:8080 -H "X-Real-IP=\$remote_addr"
```

#### 2. 文件服务器模式

```bash
# 启动文件服务器
LyWaf file -l 8080 -r /var/www/html

# 启用目录浏览
LyWaf file -l 8080 -r /var/www/html --browse

# 启用预压缩
LyWaf file -l 8080 -r /var/www/html -p
```

#### 3. 配置文件模式

```bash
# 前台运行
LyWaf run -c appsettings.yaml

# 后台启动
LyWaf start -c appsettings.yaml

# 停止服务
LyWaf stop -c appsettings.yaml

# 重载配置
LyWaf reload -c appsettings.yaml

# 验证配置
LyWaf validate -c appsettings.yaml
```

#### 4. 简单响应服务（调试用）

```bash
# 启动简单响应服务
LyWaf respond -l 8080 -b "Hello World" -s 200
```

## ⚖️ 负载均衡策略

LyWaf 支持以下负载均衡策略：

| 策略名称 | 说明 |
|---------|------|
| `RoundRobin` | 轮询（默认）：按顺序分发请求 |
| `Random` | 随机：随机选择服务器 |
| `LeastRequests` | 最少连接：将请求发给当前连接数最少的服务器 |
| `PowerOfTwoChoices` | 二选一：随机选两个，取负载低的那个 |
| `First` | 总是第一个：始终选择第一个可用的目标 |
| `WeightedRoundRobin` | 加权轮询：根据服务器权重分配请求 |
| `WeightedLeastConnections` | 加权最少连接：考虑权重的连接数最少算法 |
| `WeightedRandom` | 加权随机：根据权重随机选择服务器 |
| `IpHash` | IP哈希：基于客户端IP分配，确保会话保持 |
| `GenericHash` | 通用哈希：基于自定义变量进行哈希 |
| `ConsistentHash` | 一致性哈希：节点变化时最小化请求迁移 |

### 配置示例

```yaml
Clusters:
  backend:
    LoadBalancingPolicy: WeightedRoundRobin
    Destinations:
      server1:
        Address: 'http://192.168.1.10:8080/'
        Metadata:
          Weight: "3"
      server2:
        Address: 'http://192.168.1.11:8080/'
        Metadata:
          Weight: "1"
```

### 哈希策略变量

`GenericHash` 和 `ConsistentHash` 支持以下变量：

```yaml
Metadata:
  HashKey: "{IP}"              # 按客户端IP
  HashKey: "{Path}"            # 按请求路径
  HashKey: "{Query.user_id}"   # 按查询参数
  HashKey: "{Header.Authorization}"  # 按请求头
  HashKey: "{Cookie.session_id}"     # 按Cookie
```

## 🔗 连接池配置

LyWaf 使用 HttpClient 连接池管理后端连接，优化性能和资源使用：

```yaml
Clusters:
  backend:
    HttpClient:
      # 每个后端服务器的最大连接数（默认200，建议100-500）
      MaxConnectionsPerServer: 200
      # 请求超时时间
      RequestTimeout: '00:00:30'
      # SSL协议版本
      SslProtocols: 'Tls12, Tls13'
      # 是否允许不受信任的SSL证书（生产环境应为false）
      DangerousAcceptAnyServerCertificate: false
```

### 连接池特性

| 配置项 | 默认值 | 说明 |
|-------|-------|------|
| `MaxConnectionsPerServer` | 200 | 每个后端服务器的最大并发连接数 |
| `PooledConnectionIdleTimeout` | 2分钟 | 空闲连接的存活时间 |
| `PooledConnectionLifetime` | 10分钟 | 连接的最大生存时间 |
| `EnableMultipleHttp2Connections` | true | 启用HTTP/2多路复用 |

## 🔐 统一访问控制

LyWaf 提供统一的访问控制服务，整合 IP 访问控制和地理位置访问控制。**白名单 IP 直接放行，不进行 GeoIp 检查**，提高性能和灵活性。

### 配置示例

```yaml
AccessControl:
  # 拒绝访问时返回的 HTTP 状态码
  RejectStatusCode: 403
  # 拒绝访问时返回的消息
  # 支持占位符: {ClientIp}, {Path}, {Method}, {Host}, {Time}, {Country}, {Region}, {City}, {Isp}
  RejectMessage: "Access Denied: {ClientIp}"

  # 全局 IP 白名单（支持 CIDR）- 直接放行，不检查 IpControl、GeoControl
  Whitelist:
    - 127.0.0.1
    - 10.0.0.0/8        # 10.x.x.x 内网
    - 192.168.0.0/16    # 192.168.x.x 内网

  # =============== IP 黑名单访问控制 ===============
  IpControl:
    Enabled: true
    # IP 黑名单（支持 CIDR）
    Blacklist:
      - 1.2.3.4           # 单个 IP
      - 1.2.3.0/24        # 1.2.3.0 - 1.2.3.255
    # 基于路径的规则
    PathRules:
      /admin/*:
        Whitelist:
          - 192.168.0.0/16
        Blacklist: []

  # =============== 地理位置访问控制 ===============
  GeoControl:
    Enabled: false
    DatabasePath: "ip2region.xdb"  # IP2Region 数据库路径
    Mode: Deny  # Allow 或 Deny
    RejectMessage: "Access denied from your region: {Country}"
    # 禁止访问的国家/地区（Deny 模式）
    DenyCountries:
      - 朝鲜
      - 伊朗
    # 允许访问的国家/地区（Allow 模式）
    AllowCountries:
      - 中国
      - 美国
    # 基于路径的规则
    PathRules:
      /admin/*:
        Whitelist:
          - 中国
        Blacklist: []

  # =============== 连接限制 ===============
  ConnectionLimit:
    Enabled: false
    MaxConnectionsPerIp: 100          # 每个 IP 最大连接数
    MaxConnectionsPerDestination: 1000 # 每个后端最大连接数
    MaxTotalConnections: 10000        # 全局最大连接数
    RejectStatusCode: 503
    RejectMessage: "Too Many Connections: {ClientIp}"
    PathLimits:                       # 基于路径的连接限制
      /api/heavy/*: 10
      /download/*: 50
```

### IP 访问控制

- **白名单**（`Whitelist`）：在 `AccessControl` 顶层配置，白名单中的 IP 直接放行，不受任何访问控制限制
- **黑名单**（`IpControl.Blacklist`）：黑名单中的 IP 将被拒绝访问

### CIDR 格式说明

| 格式 | 说明 | IP 范围 |
|------|------|---------|
| `192.168.1.1` | 单个 IP | 192.168.1.1 |
| `192.168.1.0/24` | /24 网段 | 192.168.1.0 - 192.168.1.255 (256个IP) |
| `192.168.0.0/16` | /16 网段 | 192.168.0.0 - 192.168.255.255 (65536个IP) |
| `10.0.0.0/8` | /8 网段 | 10.0.0.0 - 10.255.255.255 |

### 地理位置访问控制

基于 IP2Region 实现高性能 IP 地理位置查询，支持按国家、省份、城市限制访问。

**数据库下载**: 从 [IP2Region GitHub](https://github.com/lionsoul2014/ip2region/tree/master/data) 下载 `ip2region.xdb` 文件。

| 类型 | 示例 |
|-----|------|
| 国家 | 中国、美国、日本 |
| 省份 | 广东省、北京、浙江省 |
| 城市 | 深圳市、上海市、杭州市 |

### 消息占位符

| 占位符 | 说明 |
|-------|------|
| `{ClientIp}` | 客户端 IP 地址 |
| `{Path}` | 请求路径 |
| `{Method}` | 请求方法 |
| `{Host}` | 请求 Host |
| `{Time}` | 当前时间 |
| `{Country}` | 国家名称 |
| `{Region}` | 省份/地区 |
| `{City}` | 城市 |
| `{Isp}` | 运营商 |

## 🔑 自动证书管理 (ACME/Let's Encrypt)

LyWaf 内置 ACME 客户端，支持自动申请和续期 Let's Encrypt 免费 HTTPS 证书。

### 配置示例

```yaml
Acme:
  # 是否启用 ACME 自动证书管理
  Enabled: true
  # 联系邮箱（必填）
  Email: "admin@example.com"
  # 是否同意 Let's Encrypt 服务条款（必须为 true）
  AcceptTermsOfService: true
  # 需要申请证书的域名列表
  Domains:
    - example.com
    - www.example.com
  # 证书存储目录
  CertificatePath: "certs"
  # 证书有效期剩余天数小于此值时自动续期
  RenewBeforeDays: 30
  # 是否使用测试环境（开发时建议开启）
  UseStaging: false
```

### 使用步骤

1. **配置域名**：将域名的 DNS A 记录指向服务器 IP
2. **开启 HTTP 端口**：确保 80 端口可访问（用于 HTTP-01 验证）
3. **配置 Acme**：设置 `Enabled: true`，填写邮箱和域名
4. **同意服务条款**：设置 `AcceptTermsOfService: true`
5. **启动服务**：LyWaf 会自动申请证书

### 注意事项

- **HTTP-01 验证**：Let's Encrypt 需要通过 HTTP 80 端口验证域名所有权
- **速率限制**：Let's Encrypt 有[速率限制](https://letsencrypt.org/docs/rate-limits/)，测试时请使用 `UseStaging: true`
- **证书续期**：证书有效期 90 天，会在到期前 30 天自动续期
- **通配符证书**：目前仅支持单域名证书，不支持通配符

## 📦 响应压缩

LyWaf 支持 **Gzip** 和 **Brotli** 响应压缩，根据响应大小和 MIME 类型智能决定是否压缩。

### 配置示例

```yaml
Compress:
  # 是否启用响应压缩
  Enabled: true
  # 是否启用 Brotli 压缩（优先于 Gzip，压缩率更高）
  EnableBrotli: true
  # 是否启用 Gzip 压缩
  EnableGzip: true
  # 压缩级别: Fastest, Optimal, NoCompression, SmallestSize
  Level: Fastest
  # 最小响应大小（字节），小于此值不压缩（默认 10KB）
  MinSize: 10240
  # 是否启用 HTTPS 压缩
  EnableForHttps: true
  # 需要压缩的 MIME 类型
  MimeTypes:
    - text/html
    - text/css
    - text/javascript
    - application/json
    - application/javascript
    - application/xml
    - image/svg+xml
```

### 压缩算法

| 算法 | 编码名称 | 说明 |
|------|---------|------|
| Brotli | `br` | 压缩率更高，优先使用 |
| Gzip | `gzip` | 兼容性更好，Brotli 不可用时使用 |

### 压缩条件

响应需同时满足以下条件才会被压缩：

1. `Enabled` 为 `true`
2. 客户端请求头包含 `Accept-Encoding: gzip`
3. 响应大小 >= `MinSize`
4. 响应 `Content-Type` 在 `MimeTypes` 列表中
5. 响应状态码为 2xx
6. 响应尚未被压缩（无 `Content-Encoding` 头）

### 压缩级别

| 级别 | 说明 |
|------|------|
| `Fastest` | 最快压缩速度，压缩率较低 |
| `Optimal` | 平衡速度和压缩率 |
| `SmallestSize` | 最高压缩率，速度较慢 |
| `NoCompression` | 不压缩（仅用于测试） |

## 📝 LyWaf 配置格式 (.ly)

LyWaf 支持类似 [Caddy](https://caddyserver.com/docs/caddyfile/concepts) 的简洁配置格式，文件扩展名为 `.ly`。

### 使用方法

```bash
# 使用 .ly 配置文件启动
LyWaf run -c config.ly

# 或者使用传统 YAML 格式
LyWaf run -c appsettings.yaml
```

### 核心概念

#### 站点块

以域名或地址开头，后跟花括号包含站点配置：

```ly
# 单个域名
example.com {
    reverse_proxy http://127.0.0.1:8080
}

# 多个域名
example.com www.example.com {
    reverse_proxy http://127.0.0.1:8080
}

# 端口监听（无域名限制）
:8080 {
    reverse_proxy http://127.0.0.1:9000
}

# HTTPS 站点
https://api.example.com {
    reverse_proxy http://127.0.0.1:3000
}
```

#### 地址格式

| 地址 | 说明 |
|------|------|
| `example.com` | HTTPS 站点 |
| `http://example.com` | 强制 HTTP |
| `https://example.com` | HTTPS 站点 |
| `:8080` | 监听端口（所有域名） |
| `localhost` | 本地开发 |
| `*.example.com` | 通配符域名 |

#### 全局选项块

可选的全局配置，必须放在文件最前面：

```ly
{
    email admin@example.com
    acme_staging true
    debug
}
```

#### 代码片段

可重用的配置块：

```ly
# 定义片段
(common_config) {
    lb_policy RoundRobin
}

# 使用片段
example.com {
    import common_config
    reverse_proxy http://127.0.0.1:8080
}
```

### 站点指令

| 指令 | 说明 | 示例 |
|------|------|------|
| `reverse_proxy` | 反向代理 | `reverse_proxy http://127.0.0.1:8080` |
| `lb_policy` | 负载均衡策略 | `lb_policy WeightedRoundRobin` |
| `path` | 路径匹配 | `path /api/*` |
| `tls` | TLS 证书配置 | `tls { cert = "..." key = "..." }` |

### 变量支持

```ly
# 变量定义
var domain = "example.com"
var backend = "127.0.0.1:8080"

# 使用变量
$domain www.${domain} {
    reverse_proxy http://${backend}
}
```

### 条件语句

```ly
var env = "production"

if $env == "production" {
    Logging {
        Level = "Info"
    }
} else {
    Logging {
        Level = "Debug"
    }
}
```

### 支持的特性

| 特性 | 语法 | 说明 |
|------|------|------|
| 站点块 | `domain { }` | 以域名或地址开头的配置块 |
| 变量定义 | `var name = value` | 定义配置变量 |
| 变量引用 | `$name` 或 `${name}` | 引用变量值 |
| 环境变量 | `$ENV_VAR` | 自动读取环境变量 |
| 条件语句 | `if condition { }` | 条件配置 |
| 代码片段 | `(name) { }` | 可重用配置块 |
| 导入文件 | `import "file.ly"` | 导入其他配置文件 |
| 块配置 | `Name { key = value }` | 嵌套配置块 |
| 数组 | `[item1, item2]` | 列表值 |
| 注释 | `# comment` | 单行注释 |

### 完整示例

```ly
# 变量
var domain = "example.com"

# 全局选项
{
    email admin@example.com
}

# 主站点
$domain www.${domain} {
    reverse_proxy http://127.0.0.1:8080
    lb_policy RoundRobin
}

# API 站点
api.${domain} {
    reverse_proxy http://127.0.0.1:3001 http://127.0.0.1:3002
    lb_policy WeightedRoundRobin
}

# 其他配置
Compress {
    Enabled = true
    MinSize = 10240
}
```

参考 `config.ly.example` 文件查看更多配置示例。

## 🚦 流量控制

### 请求限速

支持多种限速算法：

```yaml
SpeedLimit:
  Limits:
    # 固定窗口
    Fixed:
      Name: Fixed
      PermitLimit: 100
      Window: "00:01:00"
      
    # 滑动窗口
    Sliding:
      Name: Sliding
      PermitLimit: 100
      Window: "00:01:00"
      SegmentsPerWindow: 10
      
    # 令牌桶
    Token:
      Name: Token
      PermitLimit: 100
      ReplenishmentPeriod: "00:00:10"
      TokensPerPeriod: 20
      
    # 并发限制
    Concurrency:
      Name: Concurrency
      PermitLimit: 10
```

### 连接限制

连接限制已整合到 `AccessControl` 配置中，详见上方 [统一访问控制](#-统一访问控制) 部分。

```yaml
AccessControl:
  ConnectionLimit:
    Enabled: true
    MaxConnectionsPerIp: 100        # 每 IP 最大连接数
    MaxConnectionsPerDestination: 1000  # 每后端最大连接数
    MaxTotalConnections: 10000      # 全局最大连接数
    PathLimits:
      /api/heavy/*: 10
      /download/*: 50
```

### 带宽限速

```yaml
SpeedLimit:
  Throttled:
    Global: 1024      # 全局限速 KB/s
    Everys:
      /api/*: 100     # 路径限速 KB/s
      /file/*: 50
    IpEverys:
      192.168.1.100: 500  # IP限速 KB/s
```

## 💚 健康检查

LyWaf 提供强大的主动健康检查功能：

```yaml
Clusters:
  backend:
    HealthCheck:
      Active:
        Enabled: true
        Interval: '00:00:10'
        Timeout: '00:00:10'
        Policy: LyxActiveHealth
        Path: /api/health
        Query: check=true
    Metadata:
      LyxActiveHealth.Fails: 2      # 连续失败次数标记不健康
      LyxActiveHealth.Passes: 2     # 连续成功次数标记健康
      LyxActiveHealth.Method: GET   # 请求方法
      LyxActiveHealth.AvalidCode: 2xx,3xx  # 有效状态码
      LyxActiveHealth.ContentCheck: Contains  # 内容检查方式
      LyxActiveHealth.AvalidContent: "ok"     # 期望内容
```

### 内容检查方式

| 方式 | 说明 |
|------|------|
| `Contains` | 响应包含指定内容 |
| `Match` | 响应完全匹配 |
| `JSON` | JSON 包含检查 |
| `JSONM` | JSON 完全匹配 |

## 🛡️ WAF 防护

内置 Web 攻击检测：

```yaml
Protect:
  OpenArgsCheck: true    # 检查 Query 参数
  OpenPostCheck: true    # 检查 POST 内容
  MaxRequestBodySize: 10000
  
  # 自定义检测规则（正则）
  RegexArgsList:
    - (?:union.*select)
    - (?:script.*>)
    
  RegexPostList:
    - (?:union.*select)
```

## 📊 统计与 CC 防护

```yaml
Statistic:
  PathStas:
    - /api/*
    - /user/{id}/info
    
  Config:
    fbLimit: 30           # 检测阈值
    defaultFbTime: 200    # 封禁时长(秒)
    maxFreqFbRatio: 0.8   # 频率占比阈值
    
  LimitCc:
    - Period: 60
      LimitNum: 100
      Path: /api/*
      FbTime: "00:05:00"
```

## 📁 静态文件服务

```yaml
FileProvider:
  Everys:
    /static:
      BasePath: /var/www/static
      MaxFileSize: 10240  # KB
      TryFiles:
        - $path
        - $path/
        - index.html
```

## 🔐 HTTPS 配置

支持 SNI 多证书：

```yaml
WafInfos:
  Listens:
    - Host: 0.0.0.0
      Port: 443
      IsHttps: true
      
  Certs:
    - Host: "*.example.com"
      PemFile: /path/to/example.pem
      KeyFile: /path/to/example.key
    - Host: "*.test.com"
      PemFile: /path/to/test.pem
      KeyFile: /path/to/test.key
```

## 📝 完整配置示例

参见 [appsettings.yaml](appsettings.yaml) 获取完整配置示例。

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

## 📄 许可证

本项目采用 LGPL3.0 许可证，详见 [LICENSE](LICENSE) 文件。

---

**LyWaf** - 让 Web 安全更简单 🛡️
