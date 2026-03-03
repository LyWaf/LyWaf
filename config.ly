# ============================================================
# LyWaf 配置文件 (config.ly)
# 类似 Caddy 的简洁配置格式
# ============================================================

# 变量定义
var domain = "example.com"
var backend = "127.0.0.1:8080"
var env = "production"

control_listen = "0.0.0.0:7030"

LyLog {
    Enabled = true
    
    # 全局日志配置（没有配置专属域名日志时使用）
    Global {
        Enabled = true
        AccessLog = "access_${shortdate}.log"   # 支持 NLog 变量
        ErrorLog = "error_${shortdate}.log"
        Directory = "logs"                       # 日志目录
        Level = "Info"                          # Trace, Debug, Info, Warn, Error, Fatal
        PerfLog = false                         # 高性能日志（保持文件打开）
    }
    
    # 域名日志配置（直接使用域名作为 key）
    # 主站日志 - 单独记录到 logs/localhost/
    localhost1 {
        Enabled = true
        Output = "logs/localhost"           # 日志输出目录
        Level = "Info"
        Format = "Json"
        AlsoLogToGlobal = true              # 同时记录到全局日志
        ExcludePaths = ["/health", "/favicon.ico"]  # 排除的路径
    }
}

protect {
    CcRules {
        rule1 {
            Type = FrequentAccess
            Period = 100
            Threshold = 4
            Action = Captcha
            ActionSeconds = 60
            Priority = 1
            Conditions {
                UrlPath StartsWith ["/api/", "/v2/"]
                Method Equal ["POST", "GET"]
            }
        }
    }

    WafRules {
        block-admin-post {
            Name = "拦截 admin POST 请求"
            Action = Reject
            Conditions = [
                { Field = UriPath;  Operator = StartsWith; Value = "/admin" },
                { Field = Method;   Operator = Equal;      Value = "POST"   },
            ]
        }
    }
}

AccessControl {
    RejectStatusCode = 403
    
    IpControl {
        Enabled = true
        Blacklist = ["127.0.0.11"]
    }
}

ErrorTemplate {
    ShowReason = true
}

plugins {
    enabled = true
    plugin_directory = "plugins"
    data_directory = "plugin_data"
    
    request-logger {
        enabled = true
        log_headers = false
        log_duration = true
    }

    image-process {
        Enabled = true
        SupportedFormats = ["jpg", "jpeg", "png", "webp", "gif", "bmp"]
        DefaultOutputFormat = "webp"
        DefaultQuality = 85
        MaxWidth = 4096
        MaxHeight = 4096
        EnableUrlParams = true
        CacheSeconds = 3600
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
localhost:5002
import res "aaa"

localhost:5003, localhost:5004, 0.0.0.0:5005, example.com:5006
log = "logs/xx"
/static/ {
    file_server {
        root = "./control_html"
        try_files = "$path"
        browse = true
    }
}

/health @post {
    respond "OK"
}

file_server
root = wwwroot

#proxy = {
#    to = ["http://httpbin.org@99", "https://httpbin.org@1"]
#    lb_policy = WeightedRoundRobin
#}
#import res "aaa"
#respond "hello world {HOST}:{PORT}"
#status=201
#content-type="text/plain"
#show-req=true