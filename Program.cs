using System.Diagnostics;
using LyWaf.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing.Matching;
using NLog;
using NLog.Extensions.Logging;
using System.Net;
using System.Text;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.LoadBalancing;
using LyWaf.Middleware;
using LyWaf.Transformer;
using Yarp.ReverseProxy.Transforms;
using LyWaf.Services.Files;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services;
using LyWaf.Backend;
using LyWaf.Services.Protect;

using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Net.Security;
using LyWaf.Utils;
using LyWaf.Services.WafInfo;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Compress;
using LyWaf.Services.Acme;
using LyWaf.Services.SimpleRes;
using LyWaf.Services.Dns;
using LyWaf.Config;

using System.CommandLine;
using System.CommandLine.Parsing;
using CommandLine;
using LyWaf;
using DotNetEnv;
using LyWaf.Manager;


#pragma warning disable CA1050 // 在命名空间中声明类型
public class Program
#pragma warning restore CA1050 // 在命名空间中声明类型
{
    private static readonly NLog.Logger _logger = LogManager.GetCurrentClassLogger();

    static int Main(string[] args)
    {
        Console.WriteLine("欢迎使用LyWaf, 程序启动中!");
        var parserResult = Parser.Default.ParseArguments<FileCommandOptions, ProxyCommandOptions, RunCommandOptions, StopCommandOptions, EnvironCommandOptions, ReloadCommandOptions, StartCommandOptions, ValidateCommandOptions, RespondCommandOptions>(args);
        parserResult.WithParsed<FileCommandOptions>(val =>
        {
            DoInitCommon(val);
            _logger.Info("File command: {Value}", val.ToString());
            DoStartFile(args, val);
        }).WithParsed<ProxyCommandOptions>(val =>
        {
            DoInitCommon(val);
            _logger.Info("Proxy command: {Value}", val.ToString());
            DoStartProxy(args, val);
        }).WithParsed<StartCommandOptions>(val =>
        {
            DoInitCommon(val);
            DoStart(args, val);
        }).WithParsed<RunCommandOptions>(val =>
        {
            DoInitCommon(val);
            _logger.Info("Run command: {Value}", val.ToString());
            DoStartRun(args, val);
        }).WithParsed<StopCommandOptions>(val =>
        {
            DoInitCommon(val);
            DoStop(args, val);
        }).WithParsed<EnvironCommandOptions>(val =>
        {
            DoInitCommon(val);
            DoEnviron(args, val);
        }).WithParsed<ReloadCommandOptions>(val =>
        {
            DoReload(args, val);
        }).WithParsed<ValidateCommandOptions>(val =>
        {
            DoInitCommon(val);
            DoValidate(args, val);
        }).WithParsed<RespondCommandOptions>(val =>
        {
            DoInitCommon(val);
            DoRespond(args, val);
        });
        return 0;
    }

    public static void DoInitCommon(CommonOptions common)
    {
        LogUtil.ConfigureNLog(Directory.GetCurrentDirectory(), common.AccessLog, common.ErrorLog, common.PerfLog);
    }

    public static void DoStartProxy(string[] args, ProxyCommandOptions proxy)
    {
        var builder = WebApplication.CreateBuilder(args);
        var waf = new WafInfoOptions();
        if (proxy.PemFile != null)
        {
            waf.Certs.Add(new OneCertInfo
            {
                PemFile = proxy.PemFile,
                KeyFile = proxy.KeyFile,
            });
        }
        foreach (var from in proxy.Froms)
        {
            var url = from;
            if (!from.Contains("://"))
            {
                url = "http://" + from;
            }

            if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
            {
                waf.Listens.Add(new OneLinstenInfo
                {
                    Host = uri.Host,
                    Port = uri.Port,
                    IsHttps = uri.Scheme == "https",
                });
            }
            else
            {
                _logger.Error("配置文件不是合法的监听地址: {From}", from);
                Environment.Exit(1);
            }
        }
        Dictionary<string, string> headerUp = [];
        foreach (var k in proxy.HeaderUp)
        {
            if (k.AsSpan().Count('=') != 1)
            {
                _logger.Error("不合法的Header上行配置: {Header}", k);
                Environment.Exit(1);
            }
            var vals = k.Split("=");
            headerUp.Add(vals[0].Trim(), vals[1].Trim());
        }

        Dictionary<string, string> headerDown = [];
        foreach (var k in proxy.HeaderDown)
        {
            if (k.AsSpan().Count('=') != 1)
            {
                _logger.Error("不合法的Header下行配置: {Header}", k);
                Environment.Exit(1);
            }
            var vals = k.Split("=");
            headerDown.Add(vals[0].Trim(), vals[1].Trim());
        }
        waf.HeaderDowns = headerDown;
        waf.HeaderUps = headerUp;
        var toUrl = proxy.To;
        if (!toUrl.Contains("://"))
        {
            toUrl = "http://" + toUrl;
        }
        if (!Uri.TryCreate(toUrl, UriKind.RelativeOrAbsolute, out var _))
        {
            _logger.Error("配置文件目标地址不合法: {To}", proxy.To);
            Environment.Exit(1);
        }

        builder.Configuration.AddJsonStream(CommonUtil.ObjectToStream("WafInfos", waf));

        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "proxy-route",
                ClusterId = "proxy-cluster",
                Match = new RouteMatch
                {
                    Path = "{**catch-all}"
                },
            }
        };

        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "proxy-cluster",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    {
                        "destination1", new DestinationConfig
                        {
                            Address = toUrl
                        }
                    }
                },
                // 配置负载均衡策略
                LoadBalancingPolicy = "RoundRobin"
            }
        };
        DoStartWaf(builder, proxy, routes, clusters);
    }

    public static void DoStartFile(string[] args, FileCommandOptions file)
    {
        var builder = WebApplication.CreateBuilder(args);
        var waf = new WafInfoOptions();
        if (file.PemFile != null)
        {
            waf.Certs.Add(new OneCertInfo
            {
                PemFile = file.PemFile,
                KeyFile = file.KeyFile,
            });
            waf.Listens.Add(new OneLinstenInfo
            {
                Port = file.ListenPort,
                IsHttps = true
            });
        }
        else
        {
            waf.Listens.Add(new OneLinstenInfo
            {
                Port = file.ListenPort,
            });
        }
        builder.Configuration.AddJsonStream(CommonUtil.ObjectToStream("WafInfos", waf));
        
        // 构建 FileServerOptions（新配置）
        var routeId = "fileserver_1";
        var indexFiles = file.IndexFiles.ToArray();
        var fileServerItem = new FileServerItem
        {
            BasePath = file.Root ?? Directory.GetCurrentDirectory(),
            TryFiles = file.TryFiles.ToArray(),
            Browse = file.Browse,
            PreCompressed = file.PreCompressed,
            Prefix = "/"
        };
        if (indexFiles.Length > 0)
        {
            fileServerItem.Default = [.. indexFiles];
        }
        var fileServer = new LyWaf.Services.Files.FileServerOptions();
        fileServer.Items[routeId] = fileServerItem;
        builder.Configuration.AddJsonStream(CommonUtil.ObjectToStream("FileServer", fileServer));

        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = routeId,
                ClusterId = "file-cluster",
                Match = new RouteMatch
                {
                    Path = "{**file-all}"
                },
            }
        };

        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "file-cluster",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    {
                        "destination1", new DestinationConfig
                        {
                            Address = "http://localhost"
                        }
                    }
                },
                // 配置负载均衡策略
                LoadBalancingPolicy = "RoundRobin"
            }
        };

        DoStartWaf(builder, file, routes, clusters);
    }

    public static void DoStartRun(string[] args, RunCommandOptions run)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        // 根据文件扩展名选择加载方式
        var configPath = run.Config;
        if (configPath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase))
        {
            // 加载 .ly 配置文件
            builder.Configuration.AddLyConfig(configPath, optional: false);
            _logger.Info("使用 LyWaf 配置格式: {Path}", configPath);
        }
        else
        {
            // 加载 YAML 配置文件
            builder.Configuration.AddYamlFile(configPath, optional: false, reloadOnChange: true);
        }
        
        DoStartWaf(builder, run, null, null);
    }

    public static void DoStop(string[] args, StopCommandOptions stop)
    {
        string pidFile = stop.PidFile;

        // 确保 pid 文件有 .pid 后缀
        if (!pidFile.EndsWith(".pid"))
        {
            pidFile += ".pid";
        }

        // 如果 PID 文件不存在，尝试通过 HTTP 请求停止服务
        if (!File.Exists(pidFile))
        {
            _logger.Info("PID文件不存在: {PidFile}，尝试通过 HTTP 请求停止服务...", pidFile);
            DoStopViaHttp(stop.Config);
            return;
        }

        try
        {
            string content = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(content, out int pid))
            {
                _logger.Info("PID文件格式错误: {PidFile}，尝试通过 HTTP 请求停止服务...", pidFile);
                DoStopViaHttp(stop.Config);
                return;
            }

            try
            {
                var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    _logger.Info("进程 {Pid} 已经退出", pid);
                    return;
                }

                process.Kill();
                process.WaitForExit(5000); // 等待最多5秒

                if (process.HasExited)
                {
                    _logger.Info("✓ 进程 {Pid} 已成功停止", pid);
                }
                else
                {
                    _logger.Warn("警告: 进程 {Pid} 可能未完全停止", pid);
                }
            }
            catch (ArgumentException)
            {
                _logger.Info("进程 {Pid} 不存在或已经退出", pid);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "停止进程失败: {Message}", ex.Message);
            Environment.Exit(1);
        }
    }

    private static void DoStopViaHttp(string configFile)
    {
        if (!File.Exists(configFile))
        {
            _logger.Error("配置文件不存在: {ConfigFile}，无法获取 ControlListen 信息", configFile);
            Environment.Exit(1);
            return;
        }

        // 读取配置文件获取 ControlListen
        var config = new ConfigurationBuilder()
            .AddYamlFile(configFile, optional: false)
            .Build();

        var wafInfos = new WafInfoOptions();
        config.GetSection("WafInfos").Bind(wafInfos);
        var controlListen = wafInfos.GetControlListen();

        var scheme = controlListen.IsHttps ? "https" : "http";
        var url = $"{scheme}://{controlListen.Host}:{controlListen.Port}/api/stop";

        _logger.Info("正在发送停止请求到: {Url}", url);

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = client.GetAsync(url).GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode)
            {
                var result = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _logger.Info("✓ 停止请求已发送: {Result}", result);
            }
            else
            {
                _logger.Error("停止请求失败: HTTP {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
                Environment.Exit(1);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(ex, "无法连接到服务: {Message}", ex.Message);
            Environment.Exit(1);
        }
        catch (TaskCanceledException)
        {
            _logger.Error("请求超时");
            Environment.Exit(1);
        }
    }

    public static void DoEnviron(string[] args, EnvironCommandOptions environ)
    {
        _logger.Info("=== 环境变量列表 ===");
        _logger.Info("");

        var envVars = Environment.GetEnvironmentVariables();
        var sortedKeys = envVars.Keys.Cast<string>().OrderBy(k => k).ToList();

        foreach (var key in sortedKeys)
        {
            var value = envVars[key];
            _logger.Info("{Key}={Value}", key, value);
        }

        _logger.Info("");
        _logger.Info("共 {Count} 个环境变量", sortedKeys.Count);
    }

    public static void DoReload(string[] args, ReloadCommandOptions reload)
    {
        if (!File.Exists(reload.Config))
        {
            _logger.Error("配置文件不存在: {Config}", reload.Config);
            Environment.Exit(1);
            return;
        }

        // 读取配置文件获取 ControlListen
        var config = new ConfigurationBuilder()
            .AddYamlFile(reload.Config, optional: false)
            .Build();

        var wafInfos = new WafInfoOptions();
        config.GetSection("WafInfos").Bind(wafInfos);
        var controlListen = wafInfos.GetControlListen();

        var scheme = controlListen.IsHttps ? "https" : "http";
        var url = $"{scheme}://{controlListen.Host}:{controlListen.Port}/api/reload";

        _logger.Info("正在发送重载请求到: {Url}", url);

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = client.GetAsync(url).GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode)
            {
                var result = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _logger.Info("✓ 重载请求已发送: {Result}", result);
            }
            else
            {
                _logger.Error("重载请求失败: HTTP {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
                Environment.Exit(1);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(ex, "无法连接到服务: {Message}", ex.Message);
            Environment.Exit(1);
        }
        catch (TaskCanceledException)
        {
            _logger.Error("请求超时");
            Environment.Exit(1);
        }
    }

    public static void DoValidate(string[] args, ValidateCommandOptions validate)
    {
        _logger.Info("验证配置文件: {Config}", validate.Config);
        _logger.Info("");

        if (!File.Exists(validate.Config))
        {
            _logger.Error("✗ 配置文件不存在: {Config}", validate.Config);
            Environment.Exit(1);
            return;
        }

        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            // 读取配置文件
            var config = new ConfigurationBuilder()
                .AddYamlFile(validate.Config, optional: false)
                .Build();

            _logger.Info("✓ YAML 语法正确");

            // 验证 WafInfos 配置
            var wafInfos = new WafInfoOptions();
            config.GetSection("WafInfos").Bind(wafInfos);

            if (wafInfos.Listens.Count == 0)
            {
                warnings.Add("WafInfos.Listens 为空，将使用默认监听地址 127.0.0.1:7030");
            }
            else
            {
                foreach (var listen in wafInfos.Listens)
                {
                    if (string.IsNullOrEmpty(listen.Host))
                    {
                        errors.Add("WafInfos.Listens Host 不能为空");
                    }
                    else if (!IPAddress.TryParse(listen.Host, out _))
                    {
                        errors.Add($"WafInfos.Listens Host 不是有效的 IP 地址: {listen.Host}");
                    }
                    if (listen.Port <= 0 || listen.Port > 65535)
                    {
                        errors.Add($"WafInfos.Listens 端口无效: {listen.Port}");
                    }
                    if (listen.IsHttps && wafInfos.Certs.Count == 0)
                    {
                        errors.Add($"WafInfos.Listens 配置了 HTTPS 但没有证书配置");
                    }
                }
                _logger.Info("✓ WafInfos.Listens 配置了 {Count} 个监听地址", wafInfos.Listens.Count);
            }

            // 验证证书配置
            foreach (var cert in wafInfos.Certs)
            {
                if (string.IsNullOrEmpty(cert.PemFile))
                {
                    errors.Add("证书配置缺少 PemFile");
                }
                else if (!File.Exists(cert.PemFile))
                {
                    errors.Add($"证书文件不存在: {cert.PemFile}");
                }

                if (!string.IsNullOrEmpty(cert.KeyFile) && !File.Exists(cert.KeyFile))
                {
                    errors.Add($"密钥文件不存在: {cert.KeyFile}");
                }
            }
            if (wafInfos.Certs.Count > 0)
            {
                _logger.Info("✓ WafInfos.Certs 配置了 {Count} 个证书", wafInfos.Certs.Count);
            }

            // 验证 ControlListen
            var controlListen = wafInfos.GetControlListen();
            _logger.Info("✓ ControlListen: {Host}:{Port}", controlListen.Host, controlListen.Port);

            // 验证 ReverseProxy 配置
            var reverseProxySection = config.GetSection("ReverseProxy");
            if (reverseProxySection.Exists())
            {
                var routes = reverseProxySection.GetSection("Routes").GetChildren().ToList();
                var clusters = reverseProxySection.GetSection("Clusters").GetChildren().ToList();

                if (routes.Count == 0)
                {
                    warnings.Add("ReverseProxy.Routes 为空");
                }
                else
                {
                    _logger.Info("✓ ReverseProxy.Routes 配置了 {Count} 个路由", routes.Count);
                }

                if (clusters.Count == 0)
                {
                    warnings.Add("ReverseProxy.Clusters 为空");
                }
                else
                {
                    _logger.Info("✓ ReverseProxy.Clusters 配置了 {Count} 个集群", clusters.Count);
                }
            }
            else
            {
                warnings.Add("ReverseProxy 配置不存在");
            }

            // 验证 FileServer 配置
            var fileServerSection = config.GetSection("FileServer");
            if (fileServerSection.Exists())
            {
                var items = fileServerSection.GetSection("Items").GetChildren().ToList();
                _logger.Info("✓ FileServer.Items 配置了 {Count} 个文件服务", items.Count);
            }

            _logger.Info("");

            // 输出警告
            if (warnings.Count > 0)
            {
                _logger.Warn("⚠ 警告 ({Count}):", warnings.Count);
                foreach (var warning in warnings)
                {
                    _logger.Warn("  - {Warning}", warning);
                }
                _logger.Info("");
            }

            // 输出错误
            if (errors.Count > 0)
            {
                _logger.Error("✗ 错误 ({Count}):", errors.Count);
                foreach (var error in errors)
                {
                    _logger.Error("  - {Error}", error);
                }
                _logger.Info("");
                _logger.Error("配置验证失败！");
                Environment.Exit(1);
            }
            else
            {
                _logger.Info("✓ 配置验证通过！");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "✗ 配置文件解析失败: {Message}", ex.Message);
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 从命令行参数构建 SimpleResItem
    /// </summary>
    private static SimpleResItem BuildSimpleResItem(RespondCommandOptions respond, string host, int port)
    {
        // 解析 Headers
        var headers = new Dictionary<string, string>();
        foreach (var header in respond.Headers)
        {
            var parts = header.Split('=', 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim();
                headers[key] = value;
            }
            else
            {
                _logger.Warn("⚠ 忽略无效的 Header 格式: {Header}", header);
            }
        }

        // 替换变量
        var responseBody = respond.Body
            .Replace("{Port}", port.ToString())
            .Replace("{Address}", host);

        // 从 headers 中提取 Content-Type 和 charset
        var contentType = "text/plain";
        var charset = "utf-8";
        if (headers.TryGetValue("Content-Type", out var ctValue))
        {
            // 解析 Content-Type: text/html; charset=utf-8
            var ctParts = ctValue.Split(';');
            contentType = ctParts[0].Trim();
            foreach (var part in ctParts.Skip(1))
            {
                var kv = part.Trim().Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
                {
                    charset = kv[1].Trim();
                }
            }
            headers.Remove("Content-Type");
        }

        return new SimpleResItem
        {
            Body = responseBody,
            StatusCode = respond.Status,
            ContentType = contentType,
            Charset = charset,
            Headers = headers.Count > 0 ? headers : null,
            ShowReq = respond.ShowReq
        };
    }

    public static void DoRespond(string[] args, RespondCommandOptions respond)
    {
        // 解析监听地址
        string host = "127.0.0.1";
        int port = 8080;

        if (!string.IsNullOrEmpty(respond.Listen))
        {
            var parts = respond.Listen.Split(':');
            if (parts.Length == 2)
            {
                host = parts[0];
                if (!int.TryParse(parts[1], out port))
                {
                    _logger.Error("✗ 无效的端口号: {Port}", parts[1]);
                    Environment.Exit(1);
                    return;
                }
            }
            else if (parts.Length == 1)
            {
                if (int.TryParse(parts[0], out int portOnly))
                {
                    port = portOnly;
                }
                else
                {
                    host = parts[0];
                }
            }
        }

        if(port <= 0 || port > 65535) {
            _logger.Error("✗ 无效的端口号: {Port}, 不在有效的端口范围内", port);
            Environment.Exit(1);
            return;
        }

        // 构建 SimpleResItem
        var resItem = BuildSimpleResItem(respond, host, port);
        var routeId = "simpleres_1";

        _logger.Info("启动简单 HTTP 响应服务...");
        _logger.Info("  监听地址: http://{Host}:{Port}/", host, port);
        _logger.Info("  状态码: {StatusCode}", resItem.StatusCode);
        _logger.Info("  Content-Type: {ContentType}", resItem.GetFullContentType());
        _logger.Info("  返回内容: {ResponseBody}", resItem.Body);
        _logger.Info("  响应体长度: {Length} 字节", resItem.Body.Length);
        if (resItem.Headers != null && resItem.Headers.Count > 0)
        {
            _logger.Info("  自定义 Headers: {Count} 个", resItem.Headers.Count);
        }
        if (resItem.ShowReq)
        {
            _logger.Info("  显示请求头: 是");
        }
        _logger.Info("");

        var builder = WebApplication.CreateBuilder(args);

        // 构建 WafInfoOptions
        var waf = new WafInfoOptions();
        waf.Listens.Add(new OneLinstenInfo
        {
            Host = host,
            Port = port,
            IsHttps = false,
        });

        // 构建 SimpleResOptions
        var simpleRes = new SimpleResOptions();
        simpleRes.Items[routeId] = resItem;

        // 注入配置
        builder.Configuration.AddJsonStream(CommonUtil.ObjectToStream("WafInfos", waf));
        builder.Configuration.AddJsonStream(CommonUtil.ObjectToStream("SimpleRes", simpleRes));

        // 构建 YARP 路由配置（使用 simpleres_xxx 作为路由 ID）
        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = routeId,
                ClusterId = "cluster1",
                Match = new RouteMatch
                {
                    Path = "{**catch-all}"
                },
            }
        };

        // 构建虚拟 Cluster（SimpleRes 不需要真正的后端，但 YARP 需要有 Cluster）
        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "cluster1",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    {
                        "dest1", new DestinationConfig
                        {
                            Address = "http://127.0.0.1"
                        }
                    }
                }
            }
        };

        DoStartWaf(builder, respond, routes, clusters);
    }

    public static void DoStart(string[] args, StartCommandOptions start)
    {
        // 获取当前可执行文件路径
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;

        // 判断是否通过 dotnet 运行
        bool isDotnetRun = exePath != null &&
            (exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
             exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase));

        bool useExe = false;
        string? exeFilePath = null;

        if (isDotnetRun)
        {
            // 通过 dotnet 运行，使用 DLL 路径
            if (string.IsNullOrEmpty(entryAssembly))
            {
                _logger.Error("无法获取程序集路径");
                Environment.Exit(1);
                return;
            }

            // 检查同目录下是否存在对应的 EXE 文件
            exeFilePath = Path.ChangeExtension(entryAssembly, ".exe");
            if (File.Exists(exeFilePath))
            {
                // 找到对应的 EXE，使用 EXE 启动
                exePath = exeFilePath;
                useExe = true;
            }
            else
            {
                // 没有 EXE，使用 dotnet 启动
                exePath = "dotnet";
            }
        }
        else if (string.IsNullOrEmpty(exePath))
        {
            _logger.Error("无法获取当前可执行文件路径");
            Environment.Exit(1);
            return;
        }

        // 构建 run 命令的参数，将 start 替换为 run
        var runArgs = new List<string>();

        // 如果是 dotnet 运行且没有找到 EXE，先添加 DLL 路径
        if (isDotnetRun && !useExe && !string.IsNullOrEmpty(entryAssembly))
        {
            runArgs.Add("exec");
            runArgs.Add(entryAssembly);
        }

        runArgs.Add("run");
        runArgs.Add("-c");
        runArgs.Add(start.Config);

        // 添加 pid 文件参数
        if (!string.IsNullOrEmpty(start.PidFile))
        {
            runArgs.Add("--pid");
            runArgs.Add(start.PidFile);
        }

        // 添加环境变量文件
        if (!string.IsNullOrEmpty(start.EnvFile))
        {
            runArgs.Add("--env");
            runArgs.Add(start.EnvFile);
        }

        // 添加环境变量列表
        foreach (var env in start.EnvList)
        {
            runArgs.Add("-e");
            runArgs.Add(env);
        }

        // 添加证书参数
        if (!string.IsNullOrEmpty(start.PemFile))
        {
            runArgs.Add("--cert-pem");
            runArgs.Add(start.PemFile);
        }
        if (!string.IsNullOrEmpty(start.KeyFile))
        {
            runArgs.Add("--cert-key");
            runArgs.Add(start.KeyFile);
        }

        // 添加日志参数
        if (!string.IsNullOrEmpty(start.AccessLog))
        {
            runArgs.Add("--access-log");
            runArgs.Add(start.AccessLog);
        }
        if (!string.IsNullOrEmpty(start.ErrorLog))
        {
            runArgs.Add("--error-log");
            runArgs.Add(start.ErrorLog);
        }

        var arguments = string.Join(" ", runArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            _logger.Info("✓ 启动程序: {FileName} (启动参数: {Arguments})", startInfo.FileName, startInfo.Arguments);
            var process = Process.Start(startInfo);
            if (process != null)
            {
                _logger.Info("✓ 服务已在后台启动 (PID: {Pid})", process.Id);
                _logger.Info("  配置文件: {Config}", start.Config);
                _logger.Info("  PID文件: {PidFile}", start.PidFile);
                Environment.Exit(0);
            }
            else
            {
                _logger.Error("启动失败：无法创建进程");
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动失败: {Message}", ex.Message);
            Environment.Exit(1);
        }
    }

    public static PidManager? DoCreatePidManager(string pidFile)
    {
        if (pidFile.Length == 0)
        {
            return null;
        }

        try
        {
            return new PidManager(pidFile);
        }
        catch (InvalidOperationException e)
        {
            _logger.Error(e, "{Message}", e.Message);
            Environment.Exit(1);
            return null;
        }
    }

    public static void DoStartWaf(WebApplicationBuilder builder, CommonOptions common, RouteConfig[]? routes = null, ClusterConfig[]? clusters = null)
    {
        using PidManager? pidManager = DoCreatePidManager(common.PidFile);
        foreach (var env in common.EnvList)
        {
            if (env.AsSpan().Count("=") != 1)
            {
                _logger.Error("环境变量{Env}配置未包含=", env);
                Environment.Exit(1);
            }
            var vals = env.Split('=');
            Environment.SetEnvironmentVariable(vals[0], vals[1]);
        }

        if (common.EnvFile != null)
        {
            // 加载 env 文件
            Env.Load(common.EnvFile);
        }

        // 配置使用环境变量
        builder.Configuration.AddEnvironmentVariables();

        builder.Services.Configure<WafInfoOptions>(builder.Configuration.GetSection("WafInfos"));
        builder.Services.Configure<ProtectOptions>(builder.Configuration.GetSection("Protect"));
        builder.Services.Configure<SpeedLimitOptions>(builder.Configuration.GetSection("SpeedLimit"));
        builder.Services.Configure<FileProviderOptions>(builder.Configuration.GetSection("FileGlobal"));
        builder.Services.Configure<LyWaf.Services.Files.FileServerOptions>(builder.Configuration.GetSection("FileServer"));
        builder.Services.Configure<StatisticOptions>(builder.Configuration.GetSection("Statistic"));
        builder.Services.Configure<AccessControlOptions>(builder.Configuration.GetSection("AccessControl"));
        builder.Services.Configure<CompressOptions>(builder.Configuration.GetSection("Compress"));
        builder.Services.Configure<AcmeOptions>(builder.Configuration.GetSection("Acme"));
        builder.Services.Configure<SimpleResOptions>(builder.Configuration.GetSection("SimpleRes"));
        builder.Services.Configure<CustomDnsOptions>(builder.Configuration.GetSection("CustomDns"));

        // 注册自定义响应压缩中间件（支持 MinSize）
        builder.Services.AddSingleton<ResponseCompressMiddleware>();

        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IFileService, FileService>();
        builder.Services.AddSingleton<ISpeedLimitService, SpeedLimitService>();
        builder.Services.AddSingleton<IProtectService, ProtectService>();
        builder.Services.AddSingleton<IStatisticService, StatisticService>();
        builder.Services.AddSingleton<IAccessControlService, AccessControlService>();
        builder.Services.AddSingleton<IWafInfoService, WafInfoService>();
        builder.Services.AddSingleton<IAcmeService, AcmeService>();
        builder.Services.AddHostedService(sp => (AcmeService)sp.GetRequiredService<IAcmeService>());
        builder.Services.AddSingleton<ICustomDnsService, CustomDnsService>();
        builder.Services.AddSingleton<IProbingRequestFactory, LyxProbingRequestFactory>();
        builder.Services.AddSingleton<IActiveHealthCheckPolicy, LyxActiveHealthPolicy>();

        // 注册自定义负载均衡策略
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedRoundRobinPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedLeastConnectionsPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, IpHashPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, GenericHashPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedRandomPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, ConsistentHashPolicy>();

        Analysis.DoStartAnalysis();

        var wafInfos = new WafInfoOptions();
        builder.Configuration.GetSection("WafInfos").Bind(wafInfos);
        builder.Logging.AddNLog();

        var logger = LogManager.GetCurrentClassLogger();
        logger.Info("Application started");
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel((context, options) =>
        {
            // 合并主服务监听和控制台监听
            var allListens = wafInfos.Listens.Concat([wafInfos.GetControlListen()]);

            foreach (var url in allListens)
            {
                if (url.IsHttps)
                {
                    options.Listen(IPAddress.Parse(url.Host), url.Port, listenOptions =>
                    {
                        listenOptions.UseHttps(new TlsHandshakeCallbackOptions
                        {
                            OnConnection = context =>
                            {
                                var services = ServiceLocator.GetRequiredService<IWafInfoService>() ?? throw new Exception("HTTPS证书暂未加载");
                                var name = context.ClientHelloInfo.ServerName;
                                var cert = services.GetCertByName(name);
                                return new ValueTask<SslServerAuthenticationOptions>(new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = cert
                                });
                            },
                        });
                    });
                }
                else
                {
                    options.Listen(IPAddress.Parse(url.Host), url.Port);
                }
            }
        });

        // 注册端口匹配策略，解决多端口路由的 AmbiguousMatchException
        builder.Services.AddSingleton<MatcherPolicy, PortMatcherPolicy>();
        
        // 获取自定义 DNS 配置
        var customDnsOptions = new CustomDnsOptions();
        builder.Configuration.GetSection("CustomDns").Bind(customDnsOptions);
        var customDnsEnabled = customDnsOptions.Enabled && customDnsOptions.Entries.Count > 0;
        
        var reverse = builder.Services.AddReverseProxy()
            .ConfigureHttpClient((context, handler) =>
            {
                // SSL/TLS 配置
                handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

                // 连接池配置
                // 每个服务器的最大连接数（从配置读取，默认200）
                handler.MaxConnectionsPerServer = context.NewConfig.MaxConnectionsPerServer ?? 200;

                // 启用 HTTP/2 多路复用（减少连接数）
                handler.EnableMultipleHttp2Connections = true;

                // 连接池中空闲连接的生存时间（默认2分钟）
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);

                // 连接的最大生存时间（防止连接过旧，默认无限）
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);

                // 自定义 DNS 解析（延迟获取服务，支持配置热更新）
                if (customDnsEnabled)
                {
                    handler.ConnectCallback = CustomDnsConnectCallbackFactory.Create();
                }
            }).AddTransforms<WafTrans>();
        if (routes != null && clusters != null)
        {
            // builder.Services.AddSingleton<IProxyConfigProvider, InMemoryConfigProvider>();
            reverse.LoadFromMemory(routes, clusters);
        }
        else
        {
            reverse.LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
        }
        var app = builder.Build();

        // 初始化 ServiceLocator，确保自定义 DNS 等服务可以通过 ServiceLocator 获取
        ServiceLocator.Initialize(app.Services);

        // 注册控制台 API
        app.MapControlApi(wafInfos);

        // ACME HTTP-01 挑战中间件（必须在 HTTPS 重定向之前）
        app.UseAcmeChallenge();

        // 反向代理仅处理非 ControlListen 端口的请求
        var proxyPorts = wafInfos.Listens.Select(l => $"*:{l.Port}").ToArray();
        
        app.MapReverseProxy(proxyApp =>
        {
            // 启用响应压缩（必须放在其他中间件之前）
            proxyApp.UseMiddleware<ResponseCompressMiddleware>();
            
            // IP访问控制和连接限制（应放在较前面位置）
            proxyApp.UseMiddleware<AccessControlMiddleware>();
            // 自动 HTTPS 重定向
            proxyApp.UseMiddleware<AutoHttpsMiddleware>();
            proxyApp.UseMiddleware<WafControlMiddleware>();
            proxyApp.UseMiddleware<StatisticLogMiddleware>();
            proxyApp.UseMiddleware<ThrottledMiddleware>();
            proxyApp.UseMiddleware<SpeedLimitMiddleware>();
            // SimpleRes 简单响应处理中间件
            proxyApp.UseMiddleware<SimpleResMiddleware>();
            proxyApp.UseMiddleware<FileProviderMiddleware>();
        }).RequireHost(proxyPorts);
        app.Run();
    }
}
