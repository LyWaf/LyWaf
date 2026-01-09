using CommandLine;

namespace LyWaf;

/// <summary>
/// 通用命令行选项基类，包含大多数子命令共有的参数。
/// </summary>
public class CommonOptions
{
    /// <summary>
    /// 指定 Env 文件路径（通过 `--env`）。
    /// </summary>
    [Option("env", HelpText = "Env文件")]
    public string? EnvFile { get; set; } = null;

    /// <summary>
    /// 以 `key=value` 形式传入的环境变量列表（可多次使用 `-e`/`--environ`）。
    /// </summary>
    [Option('e', "environ", HelpText = "设置环境变量以等号隔开")]
    public IEnumerable<string> EnvList { get; set; } = [];

    /// <summary>
    /// 指定 pid 文件路径（默认：lyxwqf.pid）。
    /// </summary>
    [Option("pid", HelpText = "pid文件")]
    public string PidFile { get; set; } = "lyxwqf.pid";

    /// <summary>
    /// PEM 格式证书文件路径（用于 TLS）。
    /// </summary>
    [Option("cert-pem", HelpText = "pem证书")]
    public string? PemFile { get; set; } = null;

    /// <summary>
    /// 私钥文件路径（用于 TLS）。
    /// </summary>
    [Option("cert-key", HelpText = "key证书")]
    public string? KeyFile { get; set; } = null;

    /// <summary>
    /// 是否启用高性能日志记录（开关）。
    /// </summary>
    [Option("perf-log", HelpText = "高性能日志")]
    public bool PerfLog { get; set; } = false;

    /// <summary>
    /// access 日志输出路径（可选）。
    /// </summary>
    [Option("access-log", HelpText = "access日志记录")]
    public string? AccessLog { get; set; } = null;

    /// <summary>
    /// error 日志输出路径（可选）。
    /// </summary>
    [Option("error-log", HelpText = "error日志记录")]
    public string? ErrorLog { get; set; } = null;
}

/// <summary>
/// 文件服务器子命令的选项。
/// </summary>
[Verb("file", HelpText = "文件服务器")]
public class FileCommandOptions : CommonOptions
{
    /// <summary>
    /// 监听端口（默认：8837）。
    /// </summary>
    [Option('l', "listen", HelpText = "监听的地址")]
    public int ListenPort { get; set; } = 8837;

    /// <summary>
    /// 是否启用文件浏览（目录列表）。
    /// </summary>
    [Option("browse", HelpText = "是否启用文件浏览")]
    public bool Browse { get; set; } = false;

    /// <summary>
    /// 文件服务器根目录。
    /// </summary>
    [Option('r', "root", HelpText = "根目录")]
    public string? Root { get; set; } = null;

    /// <summary>
    /// 目录域名（用于多域名映射）。
    /// </summary>
    [Option('d', "domain", HelpText = "目录域名")]
    public string? Domain { get; set; } = null;

    /// <summary>
    /// 是否启用调试模式（输出更多调试信息）。
    /// </summary>
    [Option('v', "--debug", HelpText = "是否启用调试")]
    public bool Debug { get; set; } = false;

    /// <summary>
    /// 禁用响应压缩（开关）。
    /// </summary>
    [Option("no-compress", HelpText = "不启用压缩")]
    public bool NoCompress { get; set; } = false;

    /// <summary>
    /// 是否启用预压缩资源支持（开关）。
    /// </summary>
    [Option('p', "precompressed", HelpText = "是否启用预压缩")]
    public bool PreCompressed { get; set; } = false;

    /// <summary>
    /// 尝试的备选路径列表（用于单页应用或回退）。
    /// </summary>
    [Option("try", HelpText = "尝试路径")]
    public IEnumerable<string> TryFiles { get; set; } = [];

    /// <summary>
    /// 默认首页文件名列表（例如 index.html）。
    /// </summary>
    [Option("index", HelpText = "主页路径,例如index.html, index.txt")]
    public IEnumerable<string> IndexFiles { get; set; } = ["index.html", "index.htm"];
}

/// <summary>
/// 代理（转发）子命令的选项。
/// </summary>
[Verb("proxy", HelpText = "文件服务器")]
public class ProxyCommandOptions : CommonOptions
{
    /// <summary>
    /// 本地监听地址集合（可多次指定）。
    /// </summary>
    [Option('f', "from", HelpText = "监听地址,可多个")]
    public IEnumerable<string> Froms { get; set; } = [];

    /// <summary>
    /// 目标转发地址（例如 upstream 地址）。
    /// </summary>
    [Option('t', "to", HelpText = "转发方向")]
    public string To { get; set; } = "";

    /// <summary>
    /// 上行时要添加/修改的请求头。
    /// </summary>
    [Option('H', "header-up", HelpText = "向上的header")]
    public IEnumerable<string> HeaderUp { get; set; } = [];

    /// <summary>
    /// 下行时要添加/修改的响应头。
    /// </summary>
    [Option('d', "header-down", HelpText = "下行的header")]
    public IEnumerable<string> HeaderDown { get; set; } = [];
}

/// <summary>
/// 运行命令（加载并运行服务）的选项。
/// </summary>
[Verb("run", HelpText = "运行命令")]
public class RunCommandOptions : CommonOptions
{
    /// <summary>
    /// 指定配置文件路径（默认：appsettings.yaml）。
    /// </summary>
    [Option('c', "config", HelpText = "加载配置地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

/// <summary>
/// 停止命令的选项（使用配置文件查找 pid）。
/// </summary>
[Verb("stop", HelpText = "停止进程")]
public class StopCommandOptions : CommonOptions
{
    /// <summary>
    /// 配置文件路径，用于读取 pid 文件地址。
    /// </summary>
    [Option('c', "config", HelpText = "配置文件地址，用于获取pid文件路径")]
    public string Config { get; set; } = "appsettings.yaml";
}

/// <summary>
/// 打印环境变量的子命令选项。
/// </summary>
[Verb("environ", HelpText = "打印环境变量")]
public class EnvironCommandOptions : CommonOptions
{
    /// <summary>
    /// 配置文件路径（如果需要）。
    /// </summary>
    [Option('c', "config", HelpText = "配置文件地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

/// <summary>
/// 重新加载配置的子命令选项。
/// </summary>
[Verb("reload", HelpText = "重新加载配置")]
public class ReloadCommandOptions : CommonOptions
{
    /// <summary>
    /// 配置文件路径。
    /// </summary>
    [Option('c', "config", HelpText = "配置文件地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

/// <summary>
/// 后台启动服务（daemonize/后台运行）的选项。
/// </summary>
[Verb("start", HelpText = "后台启动服务")]
public class StartCommandOptions : CommonOptions
{
    /// <summary>
    /// 配置文件路径（默认：appsettings.yaml）。
    /// </summary>
    [Option('c', "config", HelpText = "加载配置地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

/// <summary>
/// 验证配置文件语法/内容的子命令选项。
/// </summary>
[Verb("validate", HelpText = "验证配置文件")]
public class ValidateCommandOptions: CommonOptions
{
    /// <summary>
    /// 配置文件路径。
    /// </summary>
    [Option('c', "config", HelpText = "配置文件地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

/// <summary>
/// 简单 HTTP 响应服务选项，用于快速返回自定义响应。
/// </summary>
[Verb("respond", HelpText = "简单HTTP响应服务")]
public class RespondCommandOptions: CommonOptions
{
    /// <summary>
    /// 响应体内容。
    /// </summary>
    [Option('b', "body", HelpText = "响应体内容")]
    public string Body { get; set; } = "";

    /// <summary>
    /// 自定义响应头列表，格式 `Key=Value`。
    /// </summary>
    [Option('H', "header", HelpText = "响应头，格式: key=value")]
    public IEnumerable<string> Headers { get; set; } = [];

    /// <summary>
    /// 返回的 HTTP 状态码（默认 200）。
    /// </summary>
    [Option('s', "status", HelpText = "HTTP状态码")]
    public int Status { get; set; } = 200;

    /// <summary>
    /// 监听地址，格式为 host:port（默认 127.0.0.1:8080）。
    /// </summary>
    [Option('l', "listen", HelpText = "监听地址，格式: host:port")]
    public string Listen { get; set; } = "127.0.0.1:8080";

    /// <summary>
    /// 是否在响应体中显示所有请求头（调试/测试用途）。
    /// </summary>
    [Option("show-req", HelpText = "是否在响应体中显示所有请求头")]
    public bool ShowReq { get; set; } = false;
}