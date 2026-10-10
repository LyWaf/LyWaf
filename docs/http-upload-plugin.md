# HTTP 上传插件：HMAC-SHA256

`http-upload` 默认禁用，接入代理请求链路。使用独立密钥签名，不再接受旧的 Password、X-Upload-Password 或原始二进制上传协议。

## 配置

宿主在代理管道最前调用 `UseLyWafPluginsInProxyFirst()`。HTTP 上传插件通过 `IPluginProxyStages` 的 `First` 阶段注册请求体限额中间件，主程序不直接注册上传业务中间件；阶段沿用插件运行状态检查，支持运行时启停。

合并到现有 Plugins 配置；Load 列表必须保留原有插件。

```text
Plugins {
    Load = ["http-upload"]
    Permissions {
        http-upload = "DataDirectory, SensitiveConfiguration"
    }
    http-upload {
        Enabled = true
        Path = "/_upload"
        Hosts = ["upload.example.com", "files.example.com"]
        RequireHttps = true
        Key = "replace-with-at-least-32-random-characters"
        SignatureWindowSeconds = 300
        NonceDirectory = "D:/lywaf-data/upload-nonces"
        AllowZip = true
        MaxUploadBytes = 104857600
        MaxExtractedBytes = 524288000
        MaxEntries = 1000
        Directory = "D:/uploads"
    }
}
```

Key 至少 32 字符，建议由密码学安全随机数生成器生成。它是敏感配置，通过部署配置注入，不提交仓库、不随请求发送。签名使用 Key 文本的 UTF-8 字节，不做 hex/Base64 解码。目录需要服务账户写权限，不能存在符号链接或 junction。

## 签名与表单

上传接口会在代理链路读取请求体之前，将 Kestrel 请求体限制设为 MaxUploadBytes 加 64 KiB 表单开销，避免默认 30000000 字节限制。仅对已启用且具备权限、域名/协议匹配的 POST 上传生效，普通接口不变。显式配置的 WAF 请求体限制、IIS 或前置代理限制仍独立生效，需要同时调整。

MaxUploadBytes 和 MaxExtractedBytes 使用 64 位字节数校验，可配置范围为 1–1099511627776（1 TiB），默认值不变。提高插件限额不会自动提高宿主请求体限制或五分钟处理期限，也不会预分配对应大小的内存；请根据磁盘空间和实际链路配置。

后台插件配置中的 Key 为只写密码框：输入至少 32 字符的新值可覆盖密钥，留空或不提交则保留已保存的密钥。配置读取不会返回原 Key，也不会回显新 Key。密钥会保存到服务器配置补丁，请保护配置文件和备份的文件权限。

Hosts 指定允许使用上传接口的域名，不带协议、端口或路径，忽略大小写及末尾的点。`Hosts = ["*"]` 允许所有域名（默认行为），空列表拒绝所有域名；不支持 `*.example.com` 子域通配。未授权域名请求上传路径返回 403，其他路径不受影响。这是 Host 匹配，不替代 HMAC 身份认证；域名仍需先匹配宿主代理路由才能进入插件。

POST multipart/form-data，按以下顺序提交字段，文件必须最后提交：

| 字段 | 含义 |
| --- | --- |
| filename | 安全的单个文件名，同时作为最终文件或解压目录名 |
| size | 文件实际字节数，非整个表单大小；十进制，无前导零，允许 0 |
| timestamp | Unix 秒时间戳，十进制，无前导零 |
| nonce | 每次请求新生成；32–128 个 ASCII 字母、数字、`-`、`_`，建议 16 随机字节的 hex |
| signature | HMAC-SHA256 的 64 位 hex，大小写均可 |
| extract | 可选，true 或 false，默认 false |
| directory | 可选，相对于配置 Directory 的子目录，如 cc 或 cc/releases；不能是绝对路径或包含 .. |
| file | 唯一文件，multipart filename 必须与 filename 字段一致 |

签名消息严格为 UTF-8 编码的 `filename + "\n" + size + "\n" + timestamp + "\n" + nonce`，使用 LF，不是 CRLF，最后没有换行。签名不包含字段名、引号或 multipart 边界。重复字段、多文件、文件后的字段均拒绝。

服务端先校验签名、时间窗口，再原子占用 nonce，最后流式读取文件并核对实际大小。失败上传也消耗已认证 nonce，重试必须重新生成。不会在确认全部表单合法前发布文件。

## PowerShell 调用示例

```powershell
$file = Get-Item ./report.pdf
$name = $file.Name
$size = $file.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
$nonce = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
$message = "$name`n$size`n$timestamp`n$nonce"
$signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($env:UPLOAD_KEY), [Text.Encoding]::UTF8.GetBytes($message)))
curl.exe --fail-with-body "https://upload.example.com/_upload" `
  --form-string "filename=$name" --form-string "size=$size" `
  --form-string "timestamp=$timestamp" --form-string "nonce=$nonce" `
  --form-string "signature=$signature" `
  --form-string "extract=false" -F "file=@$($file.FullName);filename=$name"
```

示例需要支持上述 .NET 密码学 API 的 PowerShell 7。不需要目录别名或 target 字段。旧的 Directories 配置不再使用。

在 file 前增加 `--form-string "directory=cc"`：普通文件 a.zip 保存为 `Directory/cc/a.zip`；同时设置 extract=true，则包内内容直接解压到 `Directory/cc/`，不会添加 a.zip 子目录。ZIP 内原有目录结构保持不变，不自动剥离包内顶层文件夹。

directory 未提供时：普通文件写入 `Directory/<filename>`，ZIP 解压到 `Directory/<filename>/`。普通上传允许使用已有子目录，但不覆盖同名文件。

## 物理目录替换

ZIP 发布允许替换已有目标目录，当前目录直接包含完整文件，不再使用版本指针。以 directory=cs 为例：

```text
D:/uploads/
    cs/index.html                 # 当前版本，可直接作为静态文件目录
    cs/assets/                    # 当前资源
    .cs_version/<version-id>/      # 上一个版本的完整备份
```

- ZIP 先完整解压并校验，再把旧 cs 移到同级 `.cs_version/<随机ID>`，将新目录移到 cs；第二步失败尝试把备份移回。旧文件不会混入新版本。
- `.cs_version` 保留最近一次旧版本，后续成功发布清理更早的、由本程序命名的版本目录。清理失败记录日志，未知名称不删除。
- LyWaf 文件服务可直接将 BasePath 配置为 `D:/uploads/cs` 或其父目录；读取租约在发送结束后释放，发布最多等待已有读取 30 秒，切换期间阻止本进程的新读取。超时不修改当前版本。
- 拒绝配置根目录、越界路径和符号链接/junction。`.lywaf-*`、`.*_version` 为保留名称，不允许上传；LyWaf 静态浏览和请求隐藏版本管理内容。外部静态服务器也必须禁止访问这些内部目录。
- **这不是严格的文件系统原子替换。** Windows 上两次目录移动之间存在短暂窗口；外部 nginx/其他进程可以直接使用当前目录，但不受 LyWaf 的读取租约保护。不得多进程并发发布；若进程崩溃或回滚失败，可从 `.cs_version` 手动恢复。没有自动断电恢复保证。
- 旧的指针布局会在下一次成功上传时整体进入备份，新目录恢复普通文件布局；不会自动修改尚未重新发布的旧指针目录。
- 响应缓存或浏览器缓存可能仍提供先前缓存内容，发布不自动刷新这些缓存。

## 防重放与安全边界

- 默认允许服务器时间前后 300 秒，配置范围 1–3600 秒；必须同步时钟。签名采用恒定时间比较。
- nonce 记录落盘并 flush，重启保留；保存两小时，覆盖最大时间窗口及允许的未来时间偏差。过期记录在后续请求清理，单进程最多保留 10000 条，满额或写入失败拒绝上传，不驱逐仍有效记录。
- NonceDirectory 必须持久化，不能配置在上传目录或公开静态资源目录内。不要在有效窗口内删除、切换或回滚记录。多实例必须共享支持排他文件锁的持久化目录；独立磁盘部署不能提供跨实例防重放。
- **该四字段协议不认证文件内容、directory 或 extract。** 同长度内容替换不会被 HMAC 检出，因此必须使用 HTTPS 并保护签名不泄露；若需要离线内容完整性和目标/操作绑定，需要另一个协议版本加入文件摘要及目标/操作，不能静默改变本协议。
- 最多同时处理两个请求，处理设置五分钟取消期限；字段限制 1 KiB，文件流式落盘，实际字节数必须与签名一致。宿主请求体限制可能更小。
- ZIP 拒绝路径穿越、绝对路径、链接、重复文件和设备名，限制条目数、目录深度及实际解压字节数。先临时解压，再按上述目录替换流程发布。
- 服务账户目录不得允许不可信本地用户修改。进程被强制终止可能留下 `.upload-*` 临时目录，停服后清理；不要清除 nonce 记录。
- 不提供病毒扫描；不要配置为可执行目录、Web 根目录或配置目录。入口还应设置来源限制和限速。

响应：201 成功；400 非法表单/文件或存储操作失败；401 签名/时间窗口不符；403 要求 HTTPS；405 方法错误；408 超时；409 nonce 重放或目标已存在；413 超限；429 并发满；503 插件/权限/密钥配置不可用。
