# LyWaf - 临源Web应用防火墙

> 安全近在咫尺，攻击远在天涯

LyWaf 是一款基于 .NET 9 和 YARP（Yet Another Reverse Proxy）构建的高性能 Web 应用防火墙（WAF），集成了反向代理、负载均衡、安全防护、流量控制等功能于一体。

## ✨ 特性

- 🚀 **高性能反向代理** - 基于 YARP 构建，支持 HTTP/HTTPS 代理
- ⚖️ **多种负载均衡策略** - 支持 11 种负载均衡算法
- 🛡️ **WAF 安全防护** - 内置 SQL 注入、XSS 等攻击检测
- 🔒 **IP 访问控制** - 支持黑白名单，CIDR 网段匹配
- 🚦 **流量控制** - 请求限速、连接限制、带宽控制
- 💚 **健康检查** - 主动健康检查，自动剔除故障节点
- 📁 **静态文件服务** - 内置文件服务器功能
- 📊 **统计分析** - 访问统计、CC 攻击检测
- 🔐 **HTTPS 支持** - SNI 多证书、自动 HTTPS 重定向

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

## 🔒 IP 访问控制

支持基于 IP 的黑白名单访问控制，完整支持 CIDR 格式。

### 配置示例

```yaml
SpeedLimit:
  AccessControl:
    Enabled: true
    Mode: Blacklist  # Whitelist 或 Blacklist
    RejectStatusCode: 403
    RejectMessage: "Access Denied: {ClientIp}"
    
    # 白名单（支持 CIDR）
    Whitelist:
      - 127.0.0.1
      - 10.0.0.0/8        # 10.x.x.x
      - 192.168.0.0/16    # 192.168.x.x
      
    # 黑名单（支持 CIDR）
    Blacklist:
      - 1.2.3.4           # 单个 IP
      - 1.2.3.0/24        # 1.2.3.0 - 1.2.3.255
      
    # 基于路径的规则
    PathRules:
      /admin/*:
        Allow:
          - 192.168.0.0/16
        Deny: []
```

### CIDR 格式说明

| 格式 | 说明 | IP 范围 |
|------|------|---------|
| `192.168.1.1` | 单个 IP | 192.168.1.1 |
| `192.168.1.0/24` | /24 网段 | 192.168.1.0 - 192.168.1.255 (256个IP) |
| `192.168.0.0/16` | /16 网段 | 192.168.0.0 - 192.168.255.255 (65536个IP) |
| `10.0.0.0/8` | /8 网段 | 10.0.0.0 - 10.255.255.255 |

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

```yaml
SpeedLimit:
  ConnectionLimit:
    Enabled: true
    MaxConnectionsPerIp: 100        # 每IP最大连接数
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
