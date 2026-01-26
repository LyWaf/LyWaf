# ============================================================
# LyWaf 配置文件 (config.ly)
# 类似 Caddy 的简洁配置格式
# ============================================================

# 变量定义
var domain = "example.com"
var backend = "127.0.0.1:8080"
var env = "production"

plugins {
    enabled = true
    plugin_directory = "plugins"
    data_directory = "plugin_data"
    
    request-logger {
        enabled = true
        log_headers = false
        log_duration = true
    }
    
    custom-header {
        enabled = true
        headers {
            X-Powered-By = "LyWaf"
            X-Frame-Options = "SAMEORIGIN"
        }
    }
}

(res) {
    respond "hello {args[0]}"
    status 201
    show-req true
}

localhost:5003, localhost:5004, 0.0.0.0:5005, example.com:5006
/static/ {
    file_server {
        root = "./wwwroot"
        browse = true
    }
}

/health @post {
    respond "OK"
}

import res "aaa"
#respond "hello world {HOST}:{PORT}"
#status=201
#content-type="text/plain"
#show-req=true