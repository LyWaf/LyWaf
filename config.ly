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

reverse_proxy http://www.qq.com
