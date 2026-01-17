# ============================================================
# LyWaf 配置文件 (config.ly)
# 类似 Caddy 的简洁配置格式
# ============================================================

# 变量定义
var domain = "example.com"
var backend = "127.0.0.1:8080"
var env = "production"

localhost:5003
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