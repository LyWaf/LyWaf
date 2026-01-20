# ============================================================
# LyWaf 配置文件 (config.ly)
# 类似 Caddy 的简洁配置格式
# ============================================================

# 变量定义
var domain = "example.com"
var backend = "127.0.0.1:8080"
var env = "production"


StreamServer {
    Enabled = true
    ConnectTimeout = 30
    DataTimeout = 300
    
    # 列表格式 - 多个上游
    8089 = ["127.0.0.1:8080"]
}

# 正向代理服务配置（HTTP 代理、HTTPS 代理、SOCKS5 代理）
ProxyServer {
    Enabled = true
    
    # 代理认证（可选）
    # Username = "proxyuser"
    # Password = "proxypass"
    
    # 超时配置
    ConnectTimeout = 30     # 连接超时（秒）
    DataTimeout = 300       # 数据传输超时（秒）
    
    # 访问控制
    # AllowedHosts = ["*.example.com", "api.service.com"]  # 白名单
    # BlockedHosts = ["*.blocked.com"]                      # 黑名单
    
    # 端口配置
    # 支持两种格式:
    #   - 纯端口号: 8080 (监听所有地址)
    #   - host:port: 127.0.0.1:8080 (监听指定地址)
    Ports {
        # HTTP/HTTPS 代理端口（监听所有地址）
        8080 {
            EnableHttp = true       # 支持 HTTP 代理
            EnableHttps = true      # 支持 HTTPS 隧道 (CONNECT)
            EnableSocks5 = true    # 不支持 SOCKS5
            RequireAuth = false     # 不需要认证
        }
        
        # SOCKS5 代理端口（仅监听本地）
        127.0.0.1:1080 {
            EnableHttp = false
            EnableHttps = false
            EnableSocks5 = true     # 支持 SOCKS5
            RequireAuth = true      # 需要认证
        }
        
        # 指定 IP 监听
        # 0.0.0.0:3128 {
        #     EnableHttp = true
        #     EnableHttps = true
        #     EnableSocks5 = true
        # }
    }
}

localhost:5003, localhost:5004, 0.0.0.0:5005, example.com:5006
/static/ {
    file_server {
        root = "./wwwroot"
        browse = true
    }
}

respond "hello world {HOST}:{PORT}"
status=201
content-type="text/plain"
show-req=true

localhost:5002
respond "Hello from {HOST}:{PORT}\nPath: {PATH}\nMethod: {METHOD}\nClient IP: {ClientIp}\nTime: {TIME}\nURL: {URL}"
status=200
content-type="text/plain"
show-req=true

example1.com:5006
respond "aaa Hello from {HOST}:{PORT}\nPath: {PATH}\nMethod: {METHOD}\nClient IP: {ClientIp}\nTime: {TIME}\nURL: {URL}"
status=200
content-type="text/plain"
show-req=true

localhost:5007
reverse_proxy http://www.baidu.com/api
