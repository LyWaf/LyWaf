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

# 端口监听 - 多路由配置示例
:5002 {
    # API 路由 - 转发到 API 服务器
    /api/* {
        reverse_proxy http://127.0.0.1:3000
    }
    
    # 静态文件服务
    /static/ {
        file_server {
            root = "./wwwroot"
            browse = true
        }
    }


    if $env != "production" {
        # 静态文件服务
        /show/*(.png|.jpg){
            file_server {
                root = "./wwwroot"
                browse = true
            }
        }
    } else {
        # 静态文件服务
        /show1/*(.png|.jpg){
            file_server {
                root = "./wwwroot"
                browse = true
            }
        }
    }
    
    
    # 默认路由（其他所有请求）
    reverse_proxy http://www.baidu.com
}
