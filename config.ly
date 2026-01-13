# ============================================================
# LyWaf 配置文件 (config.ly)
# 类似 Caddy 的简洁配置格式
# ============================================================

# 变量定义
var domain = "example.com"
var backend = "127.0.0.1:8080"

# 端口监听
:5002 {
    reverse_proxy http://www.baidu.com
    path "/{**catch-all}"
}
