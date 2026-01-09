健康检查器，超时，失败次数，返回状态码判断  check interval=3000 rise=2 fall=5 timeout=1000 type=http; url

全局分享定时器

需要在一个本地端可连，且可配置分布式的规则

检查各种常见的注入等需求

可以接受远程库的导入

本地高效的文件服务器
配置{**file-all}及FileProvider来进行配置, 前缀匹配到预期的目录路由,  利用中件间来进行处理, 提前截取映射, 利用host加前缀可以优先匹配文件目录,如127.0.0.1$/file优先级会高于/file的优先级,适合冲突时的配置

try_files

X-Forward-For如果首层不设置可能由客户端进行伪造

请求速度限制
https://learn.microsoft.com/zh-cn/aspnet/core/performance/rate-limit?view=aspnetcore-9.0

配置X-Forward-For

配置限速

统计各api的消耗速度, api可以根据情况做聚合匹配, 如/api/{user}/get可以归成一类

然后统计各负载的响应速度, 可以衡量这个的健康与否

当前节点的平均耗时时间

在线ip数据

禁止ip登陆

请求错误列表

黑名单/白名单

URL白名单, 过白, 不参与CC计算

渗透攻击测试

配置CC防护规则

路径匹配算法

流量速度

攻击参数验证

证书匹配需要pfx格式

添加命令行解析

静态返回

静态文件命令

run
start
proxy
stop
environ
reload
