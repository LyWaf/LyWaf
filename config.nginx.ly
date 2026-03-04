# ============================================================
# LyWaf 配置 — 基于 nginx.conf 转换
# ============================================================

# 监听端口
http_port = 80
https_port = 443

# 控制台监听
control_listen = "127.0.0.1:7030"

# ============================================================
# 证书配置
# ============================================================
Certs {
    # qmwpdk.net 域名证书
    wfish.qmwpdk.net {
        PemFile = "temp/certs/qmwpdk.net.pem"
        KeyFile = "temp/certs/qmwpdk.net.key"
    }
    wfishhttp.qmwpdk.net {
        PemFile = "temp/certs/qmwpdk.net.pem"
        KeyFile = "temp/certs/qmwpdk.net.key"
    }

    # qmwpdk.com 域名证书
    wfish.qmwpdk.com {
        PemFile = "temp/certs/qmwpdk.com.pem"
        KeyFile = "temp/certs/qmwpdk.com.key"
    }
    wfishhttp.qmwpdk.com {
        PemFile = "temp/certs/qmwpdk.com.pem"
        KeyFile = "temp/certs/qmwpdk.com.key"
    }
}

# ============================================================
# 通用请求头（转发客户端真实信息）
# ============================================================
(proxy_headers) {
    HeaderUps {
        X-Real-IP = "{ClientIp}"
        X-Forwarded-For = "{ClientIp}"
        X-Forwarded-Proto = "{Scheme}"
    }
}

# ============================================================
# wfish 站点 — WebSocket + 反向代理
# upstream: 172.24.200.2:11337, 172.24.200.4:11337
# ============================================================
wfish.qmwpdk.net wfish.qmwpdk.com {
    import proxy_headers
    proxy = ["http://47.98.167.46:11337", "http://47.98.167.46:11337"]
    lb_policy = RoundRobin
}

# ============================================================
# wfishhttp 站点 — 反向代理
# upstream: 172.24.200.5:11881, 172.24.200.3:11881
# ============================================================
wfishhttp.qmwpdk.net wfishhttp.qmwpdk.com {
    import proxy_headers
    proxy = ["http://8.154.44.212:11881", "http://8.154.44.212:11881"]
    lb_policy = RoundRobin
}
