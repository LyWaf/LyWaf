using CommandLine;

namespace LyWaf;

public class CommonOptions
{
    [Option("env", HelpText = "Env文件")]
    public string? EnvFile { get; set; } = null;

    [Option('e', "environ", HelpText = "设置环境变量以等号隔开")]
    public IEnumerable<string> EnvList { get; set; } = [];

    [Option("pid", HelpText = "pid文件")]
    public string PidFile { get; set; } = "lyxwqf.pid";

    [Option("cert-pem", HelpText = "pem证书")]
    public string? PemFile { get; set; } = null;
    [Option("cert-key", HelpText = "key证书")]
    public string? KeyFile { get; set; } = null;


    [Option("perf-log", HelpText = "高性能日志")]
    public bool PerfLog { get; set; } = false;
    

    [Option("access-log", HelpText = "access日志记录")]
    public string? AccessLog { get; set; } = null;

    [Option("error-log", HelpText = "error日志记录")]
    public string? ErrorLog { get; set; } = null;
}

[Verb("file", HelpText = "文件服务器")]
public class FileCommandOptions : CommonOptions
{
    [Option('l', "listen", HelpText = "监听的地址")]
    public int ListenPort { get; set; } = 8837;
    [Option("browse", HelpText = "是否启用文件浏览")]
    public bool Browse { get; set; } = false;

    [Option('r', "root", HelpText = "根目录")]
    public string? Root { get; set; } = null;

    [Option('d', "domain", HelpText = "目录域名")]
    public string? Domain { get; set; } = null;

    [Option('v', "--debug", HelpText = "是否启用调试")]
    public bool Debug { get; set; } = false;

    [Option("no-compress", HelpText = "不启用压缩")]
    public bool NoCompress { get; set; } = false;

    [Option('p', "precompressed", HelpText = "是否启用预压缩")]
    public bool PreCompressed { get; set; } = false;
    [Option("try", HelpText = "尝试路径")]
    public IEnumerable<string> TryFiles { get; set; } = [];

    [Option("index", HelpText = "主页路径,例如index.html, index.txt")]
    public IEnumerable<string> IndexFiles { get; set; } = ["index.html", "index.htm"];
}


[Verb("proxy", HelpText = "文件服务器")]
public class ProxyCommandOptions : CommonOptions
{
    [Option('f', "from", HelpText = "监听地址,可多个")]
    public IEnumerable<string> Froms { get; set; } = [];

    [Option('t', "to", HelpText = "转发方向")]
    public string To { get; set; } = "";

    [Option('H', "header-up", HelpText = "向上的header")]
    public IEnumerable<string> HeaderUp { get; set; } = [];

    [Option('d', "header-down", HelpText = "下行的header")]
    public IEnumerable<string> HeaderDown { get; set; } = [];
}


[Verb("run", HelpText = "运行命令")]
public class RunCommandOptions : CommonOptions
{
    [Option('c', "config", HelpText = "加载配置地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

[Verb("stop", HelpText = "停止进程")]
public class StopCommandOptions : CommonOptions
{
    [Option('c', "config", HelpText = "配置文件地址，用于获取pid文件路径")]
    public string Config { get; set; } = "appsettings.yaml";
}

[Verb("environ", HelpText = "打印环境变量")]
public class EnvironCommandOptions : CommonOptions
{
    [Option('c', "config", HelpText = "配置文件地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

[Verb("reload", HelpText = "重新加载配置")]
public class ReloadCommandOptions : CommonOptions
{
    [Option('c', "config", HelpText = "配置文件地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

[Verb("start", HelpText = "后台启动服务")]
public class StartCommandOptions : CommonOptions
{
    [Option('c', "config", HelpText = "加载配置地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

[Verb("validate", HelpText = "验证配置文件")]
public class ValidateCommandOptions: CommonOptions
{
    [Option('c', "config", HelpText = "配置文件地址")]
    public string Config { get; set; } = "appsettings.yaml";
}

[Verb("respond", HelpText = "简单HTTP响应服务")]
public class RespondCommandOptions: CommonOptions
{
    [Option('b', "body", HelpText = "响应体内容")]
    public string Body { get; set; } = "";

    [Option('H', "header", HelpText = "响应头，格式: key=value")]
    public IEnumerable<string> Headers { get; set; } = [];

    [Option('s', "status", HelpText = "HTTP状态码")]
    public int Status { get; set; } = 200;

    [Option('l', "listen", HelpText = "监听地址，格式: host:port")]
    public string Listen { get; set; } = "127.0.0.1:8080";

    [Option("show-req", HelpText = "是否在响应体中显示所有请求头")]
    public bool ShowReq { get; set; } = false;
}