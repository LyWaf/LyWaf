using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
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
using LyWaf.Services.Protect;

using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Net.Security;
using LyWaf.Utils;
using LyWaf.Services.WafInfo;
using LyWaf.Services.AccessControl;
using LyWaf.Services.WebTamper;
using LyWaf.Services.ResponseCaching;
using LyWaf.Plugins.Core;
using LyWaf.Control;
using LyWaf.Services.Compress;
using LyWaf.Services.Acme;
using LyWaf.Services.SimpleRes;
using LyWaf.Services.Dns;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.StreamServer;
using LyWaf.Services.ABTest;
using LyWaf.Services.Param;
using LyWaf.Services.Captcha;
using LyWaf.Services.WafRule;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Metrics;
using LyWaf.Services.RequestMirroring;
using LyWaf.Services.ExternalAuthorization;
using LyWaf.Control.Infrastructure;
using LyWaf.Services.Protocols;
using System.Net.Quic;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Telemetry;
using LyWaf.Services.Discovery;
using LyWaf.Services.Distribution;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Retry;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;
using LyWaf.Config;
using LyWaf.Shared;

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
        var parserResult = Parser.Default.ParseArguments<FileCommandOptions, ProxyCommandOptions, RunCommandOptions, StopCommandOptions, EnvironCommandOptions, ReloadCommandOptions, StartCommandOptions, ValidateCommandOptions, RespondCommandOptions, ResetCommandOptions>(args);
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
        }).WithParsed<ResetCommandOptions>(val =>
        {
            DoReset(val);
        });
        return 0;
    }

    public static void DoInitCommon(CommonOptions common)
    {
        LogUtil.ConfigureNLog(Directory.GetCurrentDirectory(), common.AccessLog, common.ErrorLog, common.PerfLog);
    }

    /// <summary>
    /// 重置命令分发（lywaf reset &lt;target&gt;）
    /// </summary>
    public static void DoReset(ResetCommandOptions options)
    {
        switch (options.Target.ToLowerInvariant())
        {
            case "password":
                DoResetPassword();
                break;
            default:
                Console.WriteLine($"未知的重置目标: {options.Target}");
                Console.WriteLine("可用目标: password");
                break;
        }
    }

    /// <summary>
    /// 重置控制台密码（lywaf reset password）
    /// </summary>
    public static void DoResetPassword()
    {
        var authService = new LyWaf.Services.Auth.AuthService();
        authService.Initialize();
        var newPassword = authService.ResetPassword();

        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║     控制台密码已重置                 ║");
        Console.WriteLine("╠══════════════════════════════════════╣");
        Console.WriteLine($"║  用户名: LyWaf                       ║");
        Console.WriteLine($"║  新密码: {newPassword,-29}║");
        Console.WriteLine("╚══════════════════════════════════════╝");
        Console.WriteLine();
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
        var configPath = run.Config;
        SharedData.ConfigFilePath = Path.GetFullPath(configPath);

        // 重启循环：端口变更时 ControlApi 设置 RestartRequested 并 StopApplication()
        // app.Run() 返回后检测标记，重建 WebApplication 以应用新端口监听
        do
        {
            SharedData.RestartRequested = false;

            var builder = WebApplication.CreateBuilder(args);

            if (configPath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase))
            {
                builder.Configuration.AddLyConfig(configPath, optional: false);
                _logger.Info("使用 LyWaf 配置格式: {Path}", configPath);
            }
            else
            {
                builder.Configuration.AddDraftAwareYamlFile(configPath, optional: false);
            }

            // 加载负载均衡补丁（最后加载，以补丁方式覆盖主配置中的 Clusters）
            builder.Configuration.AddLbPatch();

            DoStartWaf(builder, run, null, null);

            if (SharedData.UpgradeRequested)
            {
                if (TryStartUpgradeCandidate(configPath)) return;
                SharedData.RestartRequested = true;
                _logger.Error("候选版本未能就绪，正在恢复当前程序实例");
            }

            if (SharedData.RestartRequested)
            {
                _logger.Info("检测到端口变更，正在重启 WebApplication 以应用新端口监听...");
            }
        } while (SharedData.RestartRequested);
    }

    /// <summary>
    /// 在旧 WebApplication 已完全停止并释放监听端口后启动候选版本；候选失败时返回 false 触发旧版本恢复。
    /// </summary>
    private static bool TryStartUpgradeCandidate(string configPath)
    {
        var probeProcessId = SharedData.PendingUpgradeProbeProcessId;
        SharedData.PendingUpgradeProbeProcessId = null;
        if (probeProcessId.HasValue)
        {
            try
            {
                using var probe = Process.GetProcessById(probeProcessId.Value);
                if (!probe.HasExited) { probe.Kill(entireProcessTree: true); probe.WaitForExit(5000); }
            }
            catch (ArgumentException) { }
            catch (Exception ex) { _logger.Warn(ex, "停止候选探针进程失败: {Pid}", probeProcessId.Value); }
        }
        var entryPoint = SharedData.PendingUpgradeEntryPoint;
        var version = SharedData.PendingUpgradeVersion;
        SharedData.UpgradeRequested = false;
        SharedData.PendingUpgradeEntryPoint = null;
        SharedData.PendingUpgradeVersion = null;
        if (string.IsNullOrWhiteSpace(entryPoint) || string.IsNullOrWhiteSpace(version) || !File.Exists(entryPoint))
        {
            _logger.Error("待激活候选版本信息不完整");
            return false;
        }

        IConfigurationRoot configuration;
        var configurationBuilder = new ConfigurationBuilder();
        if (configPath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase))
            configurationBuilder.AddLyConfig(configPath, optional: false);
        else
            configurationBuilder.AddYamlFile(configPath, optional: false);
        configuration = configurationBuilder.Build();
        var upgradeOptions = new LyWaf.Services.Lifecycle.UpgradeOptions();
        configuration.GetSection("Lifecycle:Upgrade").Bind(upgradeOptions);
        var store = new LyWaf.Services.Lifecycle.UpgradePackageService(
            Microsoft.Extensions.Options.Options.Create(upgradeOptions));
        var wafOptions = new WafInfoOptions();
        configuration.GetSection("WafInfos").Bind(wafOptions);
        var control = wafOptions.GetControlListen();
        var host = control.Host is "0.0.0.0" or "::" ? "127.0.0.1" : control.Host;
        var readyUrl = $"{(control.IsHttps ? "https" : "http")}://{host}:{control.Port}/api/health/ready";

        Process? candidate = null;
        try
        {
            var start = new ProcessStartInfo
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (entryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = "dotnet";
                start.ArgumentList.Add(entryPoint);
            }
            else start.FileName = entryPoint;
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(Path.GetFullPath(configPath));
            candidate = Process.Start(start) ?? throw new InvalidOperationException("候选进程未启动");

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(upgradeOptions.ReadinessTimeoutSeconds, 1, 300));
            while (DateTime.UtcNow < deadline)
            {
                if (candidate.HasExited) throw new InvalidOperationException($"候选进程提前退出，退出码 {candidate.ExitCode}");
                try
                {
                    using var response = client.GetAsync(readyUrl).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        store.CommitAsync(version).GetAwaiter().GetResult();
                        _logger.Info("候选版本 {Version} 已就绪并成为当前版本，PID={Pid}", version, candidate.Id);
                        return true;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                Thread.Sleep(250);
            }
            throw new TimeoutException($"候选版本在 {upgradeOptions.ReadinessTimeoutSeconds} 秒内未就绪");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "候选版本 {Version} 激活失败", version);
            if (candidate is { HasExited: false })
            {
                try { candidate.Kill(entireProcessTree: true); candidate.WaitForExit(5000); } catch { }
            }
            store.AbortPendingAsync().GetAwaiter().GetResult();
            return false;
        }
        finally { candidate?.Dispose(); }
    }

    public static void DoStop(string[] args, StopCommandOptions stop)
    {
        string pidFile = stop.PidFile;

        // 确保 pid 文件有 .pid 后缀
        if (!pidFile.EndsWith(".pid"))
        {
            pidFile += ".pid";
        }

        // 尝试解析 PID（用于确认优雅退出 / 兜底强杀），缺失或异常时仍可走 HTTP 优雅停止
        Process? process = null;
        if (File.Exists(pidFile))
        {
            try
            {
                string content = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(content, out int pid))
                {
                    try
                    {
                        process = Process.GetProcessById(pid);
                        if (process.HasExited)
                        {
                            _logger.Info("进程 {Pid} 已经退出", pid);
                            return;
                        }
                    }
                    catch (ArgumentException)
                    {
                        _logger.Info("进程 {Pid} 不存在或已经退出", pid);
                        return;
                    }
                }
                else
                {
                    _logger.Warn("PID文件格式错误: {PidFile}，将尝试通过 HTTP 优雅停止", pidFile);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "读取 PID 文件失败: {PidFile}，将尝试通过 HTTP 优雅停止", pidFile);
            }
        }
        else
        {
            _logger.Info("PID文件不存在: {PidFile}，将尝试通过 HTTP 优雅停止", pidFile);
        }

        // 优先优雅停止：HTTP /api/stop → StopApplication() → 触发 ApplicationStopping
        // （DuckDB 最终刷写+CHECKPOINT、插件停止等），避免硬杀导致 WAL 堆积
        bool gracefulSent = TryStopViaHttp(stop.Config);

        if (process != null)
        {
            if (gracefulSent)
            {
                // 等待优雅退出（关机超时 5s + 最终刷写余量）
                if (process.WaitForExit(15000))
                {
                    _logger.Info("✓ 进程 {Pid} 已优雅停止", process.Id);
                    return;
                }
                _logger.Warn("优雅停止超时，强制结束进程 {Pid}", process.Id);
            }
            else
            {
                _logger.Warn("无法通过 HTTP 优雅停止，回退为强制结束进程 {Pid}", process.Id);
            }

            // 兜底：强制结束
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                _logger.Info(process.HasExited ? "✓ 进程 {Pid} 已停止" : "警告: 进程 {Pid} 可能未完全停止", process.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "强制停止进程失败: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }
        else if (!gracefulSent)
        {
            // 既无可用 PID，也无法通过 HTTP 停止
            _logger.Error("停止失败：无可用 PID 且无法通过 HTTP 连接到服务");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 通过 HTTP /api/stop 请求优雅停止服务。
    /// 成功发送返回 true；无法连接/超时/配置缺失返回 false（由调用方决定是否回退强杀）。
    /// </summary>
    private static bool TryStopViaHttp(string configFile)
    {
        if (!File.Exists(configFile))
        {
            _logger.Warn("配置文件不存在: {ConfigFile}，无法通过 HTTP 优雅停止", configFile);
            return false;
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
                return true;
            }

            _logger.Warn("停止请求失败: HTTP {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn(ex, "无法连接到服务: {Message}", ex.Message);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.Warn("停止请求超时");
            return false;
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
        var reloadBuilder = new ConfigurationBuilder();
        if (reload.Config.EndsWith(".ly", StringComparison.OrdinalIgnoreCase)) reloadBuilder.AddLyConfig(reload.Config, optional: false);
        else reloadBuilder.AddYamlFile(reload.Config, optional: false);
        var config = reloadBuilder.Build();

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
            var validateBuilder = new ConfigurationBuilder();
            if (validate.Config.EndsWith(".ly", StringComparison.OrdinalIgnoreCase)) validateBuilder.AddLyConfig(validate.Config, optional: false);
            else validateBuilder.AddYamlFile(validate.Config, optional: false);
            var config = validateBuilder.Build();

            _logger.Info("✓ 配置语法正确");
            new LyWaf.Services.Configuration.ConfigurationLifecycleService().Validate(config);
            _logger.Info("✓ 路由引用和 WAF 安全策略冒烟检查通过");

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
        var responseBody = StringUtil.ReplacePlaceholders(respond.Body, new Dictionary<string, string?>
        {
            ["Port"] = port.ToString(),
            ["Address"] = host
        });

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

        var instanceDirectory = Path.GetDirectoryName(Path.GetFullPath(SharedData.ConfigFilePath))!;
        using var instanceLease = builder.Configuration.GetValue("Lifecycle:ReadinessProbe", false) ? null :
            new FileStream(Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.instance-state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (instanceLease != null && LyWaf.Services.Backup.InstanceStateCoordinator.IsPending(instanceDirectory))
            LyWaf.Services.Backup.InstanceStateCoordinator.ApplyBeforeStartup(instanceDirectory,
                LyWaf.Services.Backup.InstanceStatePaths.FromConfiguration(builder.Configuration));

        var statisticsPath = Path.GetFullPath(builder.Configuration.GetValue<string>("DuckDb:DatabasePath") ?? "bin/lywaf_stats.duckdb");
        var restoreDirectory = Path.GetDirectoryName(Path.GetFullPath(SharedData.ConfigFilePath))!;
        var statisticsEnabled = builder.Configuration.GetValue("DuckDb:Enabled", true);
        using var statisticsLease = LyWaf.Services.Backup.StatisticsRestoreCoordinator.AcquireLease(statisticsPath, statisticsEnabled);
        if (statisticsEnabled) LyWaf.Services.Backup.StatisticsRestoreCoordinator.ApplyBeforeStartup(restoreDirectory, statisticsPath);

        builder.Services.Configure<WafInfoOptions>(builder.Configuration.GetSection("WafInfos"));
        builder.Services.Configure<ProtectOptions>(builder.Configuration.GetSection("Protect"));
        builder.Services.Configure<LyWaf.Services.ResponseWaf.ResponseWafOptions>(builder.Configuration.GetSection("ResponseWaf"));
        builder.Services.Configure<LyWaf.Services.AntiBot.AntiBotOptions>(builder.Configuration.GetSection("AntiBot"));
        builder.Services.Configure<SpeedLimitOptions>(builder.Configuration.GetSection("SpeedLimit"));
        builder.Services.Configure<FileProviderOptions>(builder.Configuration.GetSection("FileGlobal"));
        builder.Services.Configure<LyWaf.Services.Files.FileServerOptions>(builder.Configuration.GetSection("FileServer"));
        builder.Services.Configure<StatisticOptions>(builder.Configuration.GetSection("Statistic"));
        builder.Services.Configure<AccessControlOptions>(builder.Configuration.GetSection("AccessControl"));
        builder.Services.Configure<CompressOptions>(builder.Configuration.GetSection("Compress"));
        builder.Services.AddOptions<AcmeOptions>().Bind(builder.Configuration.GetSection("Acme"))
            .Validate(AcmeOnDemandPolicy.IsValidOptions, "ACME/内部 CA 配置无效。")
            .ValidateOnStart();
        builder.Services.Configure<SimpleResOptions>(builder.Configuration.GetSection("SimpleRes"));
        builder.Services.Configure<CustomDnsOptions>(builder.Configuration.GetSection("CustomDns"));
        builder.Services.Configure<ProxyServerOptions>(builder.Configuration.GetSection("ProxyServer"));
        builder.Services.AddOptions<StreamServerOptions>()
            .Bind(builder.Configuration.GetSection("StreamServer"))
            .Validate(options => options.Streams.Values.All(StreamPrimitives.IsValidConfig),
                "Stream 配置包含非法上游、CIDR、协议组合或资源限制")
            .ValidateOnStart();
        builder.Services.Configure<ABTestOptions>(builder.Configuration.GetSection("ABTest"));
        builder.Services.Configure<GlobalOptions>(builder.Configuration.GetSection("Global"));
        builder.Services.Configure<LyWaf.Services.LyLog.LyLogOptions>(builder.Configuration.GetSection("LyLog"));
        builder.Services.Configure<LyWaf.Services.ErrorTemplate.ErrorTemplateOptions>(builder.Configuration.GetSection("ErrorTemplate"));
        builder.Services.Configure<MetricsOptions>(builder.Configuration.GetSection("Metrics"));
        builder.Services.AddOptions<Http3Options>().Bind(builder.Configuration.GetSection("Http3"))
            .Validate(static value => value.Ports.All(static port => port is >= 1 and <= 65535),
                "Http3.Ports 包含无效端口。")
            .ValidateOnStart();
        builder.Services.AddOptions<GrpcOptions>().Bind(builder.Configuration.GetSection("Grpc"))
            .Validate(static value => value.H2cPorts.All(static port => port is >= 1 and <= 65535),
                "Grpc.H2cPorts 包含无效端口。")
            .ValidateOnStart();
        builder.Services.AddLyWafOpenTelemetry(builder.Configuration);

        // 请求排空发生在 StopApplication 之前；这里为后台服务最终刷写保留独立超时。
        var shutdownTimeout = Math.Clamp(builder.Configuration.GetValue("Lifecycle:ShutdownTimeoutSeconds", 15), 1, 600);
        builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeout));

        // 注册自定义响应压缩中间件（支持 MinSize）
        builder.Services.AddSingleton<ResponseCompressMiddleware>();

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<LyWaf.Services.Configuration.ConfigVersionService>();
        builder.Services.AddSingleton<LyWaf.Services.Configuration.ConfigurationLifecycleService>();
        builder.Services.AddSingleton<LyWaf.Services.ManagementTasks.ManagementJobService>();
        builder.Services.AddSingleton<LyWaf.Services.Backup.ConfigurationBackupService>();
        builder.Services.AddSingleton<LyWaf.Services.Incidents.IncidentRequestStore>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.Incidents.IncidentRequestStore>());
        builder.Services.AddSingleton<LyWaf.Services.ManagementApi.ManagementIdempotency>();
        builder.Services.AddSingleton<LyWaf.Services.ManagementTasks.ManagementHealthService>();
        builder.Services.AddSingleton<LyWaf.Services.Sites.SiteStatisticsService>();
        builder.Services.AddSingleton<LyWaf.Services.Incidents.SecurityIncidentService>();
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(SharedData.ConfigFilePath))!, ".lywaf.keys")));
        builder.Services.AddHttpClient("management-notifications", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<LyWaf.Services.Notifications.NotificationService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.Notifications.NotificationService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.Incidents.SecurityIncidentService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.Sites.SiteStatisticsService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.ManagementTasks.ManagementJobService>());
        builder.Services.AddSingleton<ControlConfigurationPatchService>();
        builder.Services.AddSingleton<LyWaf.Services.Lifecycle.GracefulShutdownCoordinator>();
        builder.Services.Configure<LyWaf.Services.Lifecycle.UpgradeOptions>(builder.Configuration.GetSection("Lifecycle:Upgrade"));
        builder.Services.AddSingleton<LyWaf.Services.Lifecycle.UpgradePackageService>();
        builder.Services.AddSingleton<IFileService, FileService>();
        builder.Services.AddSingleton<ISpeedLimitService, SpeedLimitService>();
        builder.Services.AddSingleton<IProtectService, ProtectService>();
        builder.Services.AddSingleton<IRequestInspectionService, RequestInspectionService>();
        builder.Services.AddSingleton<ISemanticThreatDetector, SemanticThreatDetector>();
        builder.Services.AddSingleton<LyWaf.Services.ResponseWaf.IResponseWafDetector, LyWaf.Services.ResponseWaf.ResponseWafDetector>();
        builder.Services.AddSingleton<LyWaf.Services.AntiBot.IAntiBotAnalyzer, LyWaf.Services.AntiBot.AntiBotAnalyzer>();
        builder.Services.AddSingleton<LyWaf.Services.AntiBot.IAntiBotChallengeService, LyWaf.Services.AntiBot.AntiBotChallengeService>();
        builder.Services.AddSingleton<IStatisticService, StatisticService>();
        builder.Services.AddSingleton<ICaptchaService, CaptchaService>();
        builder.Services.AddSingleton<ICcRuleChecker, CcRuleChecker>();
        builder.Services.AddSingleton<IAccessControlService, AccessControlService>();
        builder.Services.AddSingleton<IIpGroupService, IpGroupService>();
        builder.Services.Configure<RulePackageOptions>(builder.Configuration.GetSection("Protect:RulePackages"));
        builder.Services.AddSingleton<IRulePackageService, RulePackageService>();
        builder.Services.AddSingleton<IWafRuleService, WafRuleService>();
        builder.Services.AddSingleton<IWafInfoService, WafInfoService>();
        builder.Services.AddSingleton<IAcmeService, AcmeService>();
        builder.Services.AddHostedService(sp => (AcmeService)sp.GetRequiredService<IAcmeService>());
        builder.Services.AddSingleton<ICustomDnsService, CustomDnsService>();
        builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
        builder.Services.AddLyWafMetrics();
        builder.Services.AddOptions<ManagementApiOptions>().Bind(builder.Configuration.GetSection("ManagementApi"))
            .Validate(options => options.MaxRequestBodyBytes is >= 1024 and <= 67108864 &&
                                 options.ReadRequestsPerMinute is >= 1 and <= 100000 &&
                                 options.WriteRequestsPerMinute is >= 1 and <= 100000 &&
                                 options.MaxTrackedClients is >= 100 and <= 100000,
                "ManagementApi 限制配置无效。")
            .ValidateOnStart();
        builder.Services.AddSingleton<ManagementApiGuard>();
        builder.Services.AddLyWafRequestMirroring(builder.Configuration);
        builder.Services.AddLyWafExternalAuthorization(builder.Configuration);

        // 注册 DuckDB 持久化服务
        builder.Services.Configure<LyWaf.Services.DuckDb.DuckDbOptions>(builder.Configuration.GetSection("DuckDb"));
        builder.Services.AddSingleton<LyWaf.Services.DuckDb.IDuckDbService, LyWaf.Services.DuckDb.DuckDbService>();
        builder.Services.AddSingleton<LyWaf.Services.DuckDb.DuckDbQueryService>();
        builder.Services.AddSingleton<LyWaf.Services.DuckDb.IDuckDbQueryService, MetricsDuckDbQueryService>();
        builder.Services.AddHostedService<LyWaf.Services.DuckDb.StatisticRecoveryService>();  // 启动时恢复数据（先于 Flush）
        builder.Services.AddSingleton<LyWaf.Services.DuckDb.StatisticFlushService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.DuckDb.StatisticFlushService>());
        builder.Services.AddHostedService<LyWaf.Services.DuckDb.DataRetentionService>();  // 数据保留策略
        builder.Services.AddSingleton<LyWaf.Services.DuckDb.RuntimeLogService>();  // 运行日志
        builder.Services.AddSingleton<PcapCertProvider>();       // Pcap 抓包证书提供器
        builder.Services.AddHostedService<HttpProxyService>();  // 统一代理服务（HTTP/HTTPS/SOCKS5/Pcap）
        builder.Services.AddHostedService<StreamService>();     // TCP 流代理服务
        builder.Services.AddSingleton<IProbingRequestFactory, LyxProbingRequestFactory>();
        builder.Services.AddSingleton<IActiveHealthCheckPolicy, LyxActiveHealthPolicy>();
        builder.Services.AddSingleton<OutlierDetectionRegistry>();
        builder.Services.AddSingleton<IPassiveHealthCheckPolicy, OutlierDetectionPolicy>();
        builder.Services.AddLyWafServiceDiscovery(builder.Configuration);
        builder.Services.AddLyWafDistribution(builder.Configuration);
        builder.Services.AddLyWafRuntimeState(builder.Configuration);
        builder.Services.AddLyWafResponseCache(builder.Configuration);

        // 注册自定义负载均衡策略
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedRoundRobinPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedLeastConnectionsPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, IpHashPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, GenericHashPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, WeightedRandomPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, ConsistentHashPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, AdvancedLoadBalancingPolicy>();
        
        // 注册 A/B 测试服务和策略
        builder.Services.AddSingleton<IABTestService, ABTestService>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, ABCookieTestPolicy>();
        builder.Services.AddSingleton<ILoadBalancingPolicy, ABTestWeightedPolicy>();

        // 注册域名日志服务
        builder.Services.AddSingleton<LyWaf.Services.LyLog.ILyLogService, LyWaf.Services.LyLog.LyLogService>();

        // 注册错误模板服务
        builder.Services.AddSingleton<LyWaf.Services.ErrorTemplate.IErrorTemplateService, LyWaf.Services.ErrorTemplate.ErrorTemplateService>();

        // 注册认证服务
        builder.Services.AddSingleton<LyWaf.Services.Auth.IAuthService, LyWaf.Services.Auth.AuthService>();
        builder.Services.AddSingleton<LyWaf.Services.Auth.IManagementTokenService, LyWaf.Services.Auth.ManagementTokenService>();

        // 注册插件系统
        var startupSw = Stopwatch.StartNew();
        builder.Services.AddLyWafPlugins(builder.Configuration);
        _logger.Info("[启动耗时] 插件注册: {}ms", startupSw.ElapsedMilliseconds);

        var wafInfos = new WafInfoOptions();
        builder.Configuration.GetSection("WafInfos").Bind(wafInfos);
        var managementApiOptions = new ManagementApiOptions();
        builder.Configuration.GetSection("ManagementApi").Bind(managementApiOptions);
        if (!managementApiOptions.AllowRemoteControl &&
            !WafInfoOptions.IsLoopbackAddress(wafInfos.GetControlListen().Host))
            throw new InvalidOperationException("ControlListen 使用非回环地址时必须显式设置 ManagementApi.AllowRemoteControl=true。");
        var http3Options = builder.Configuration.GetSection("Http3").Get<Http3Options>() ?? new Http3Options();
        var grpcOptions = builder.Configuration.GetSection("Grpc").Get<GrpcOptions>() ?? new GrpcOptions();
        var quicSupported = QuicConnection.IsSupported;
        if (http3Options.Enabled && !quicSupported)
        {
            if (http3Options.RequireSupport)
                throw new PlatformNotSupportedException("Http3.Enabled=true，但当前操作系统或运行时不支持 QUIC。");
            _logger.Warn("当前平台不支持 QUIC，HTTP/3 已降级关闭，HTTP/1.1 与 HTTP/2 继续可用");
        }
        builder.Logging.AddNLog();

        var logger = LogManager.GetCurrentClassLogger();
        logger.Info("Application started");
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel((context, options) =>
        {
            // 合并主服务监听和控制台监听
            var allListens = wafInfos.Listens.Select(static listen => (Listen: listen, IsControl: false))
                .Append((wafInfos.GetControlListen(), true));

            foreach (var (url, isControl) in allListens)
            {
                if (url.IsHttps)
                {
                    options.Listen(IPAddress.Parse(url.Host), url.Port, listenOptions =>
                    {
                        listenOptions.Protocols = Http3Policy.ShouldEnable(http3Options, url, isControl, quicSupported)
                            ? HttpProtocols.Http1AndHttp2AndHttp3
                            : HttpProtocols.Http1AndHttp2;
                        listenOptions.UseHttps(new TlsHandshakeCallbackOptions
                        {
                            OnConnection = async context =>
                            {
                                var services = ServiceLocator.GetRequiredService<IWafInfoService>() ?? throw new Exception("HTTPS证书暂未加载");
                                var name = context.ClientHelloInfo.ServerName;
                                var cert = services.TryGetCertByName(name);
                                if (cert is null && !string.IsNullOrWhiteSpace(name))
                                {
                                    var acme = ServiceLocator.GetService<IAcmeService>();
                                    cert = acme is null ? null : await acme.GetOrRequestCertificateAsync(name, context.CancellationToken);
                                }
                                cert ??= services.GetCertByName(name);
                                return new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = cert
                                };
                            },
                        });
                    });
                }
                else
                {
                    if(IPAddress.TryParse(url.Host, out var ip)) {
                        options.Listen(ip, url.Port, listenOptions =>
                            listenOptions.Protocols = GrpcProtocolPolicy.ForHttpListener(grpcOptions, url, isControl));
                    } else {
                        options.Listen(IPAddress.Loopback, url.Port, listenOptions =>
                            listenOptions.Protocols = GrpcProtocolPolicy.ForHttpListener(grpcOptions, url, isControl));
                    }
                }
            }
        });

        // 注册端口匹配策略，解决多端口路由的 AmbiguousMatchException
        builder.Services.AddSingleton<MatcherPolicy, PortMatcherPolicy>();
        
        // 获取自定义 DNS 配置
        var customDnsOptions = new CustomDnsOptions();
        builder.Configuration.GetSection("CustomDns").Bind(customDnsOptions);
        var customDnsEnabled = customDnsOptions.Enabled && (customDnsOptions.Entries.Count > 0 || customDnsOptions.DnsServers.Count > 0);
        
        startupSw.Restart();
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
        builder.Services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory, RetryForwarderHttpClientFactory>());
        reverse.AddConfigFilter<LyWaf.Policy.DefaultActiveHealthConfigFilter>();
        LyWaf.Services.Sites.ManagedSiteRegistration.AddManagedSites(builder.Services);
        _logger.Info("[启动耗时] YARP 配置: {}ms", startupSw.ElapsedMilliseconds);
        startupSw.Restart();
        var app = builder.Build();
        _logger.Info("[启动耗时] builder.Build(): {}ms", startupSw.ElapsedMilliseconds);

        // 同步 GlobalOptions 到 SharedData
        var globalOptions = app.Services.GetService<Microsoft.Extensions.Options.IOptionsMonitor<GlobalOptions>>();
        if (globalOptions != null)
        {
            SharedData.IsFirstServer = globalOptions.CurrentValue.IsFirstServer;
            globalOptions.OnChange((opts, _) => SharedData.IsFirstServer = opts.IsFirstServer);
        }

        // 初始化 ServiceLocator，确保自定义 DNS 等服务可以通过 ServiceLocator 获取
        ServiceLocator.Initialize(app.Services);

        // 初始化插件系统（不阻塞启动）
        var pluginManager = app.Services.GetRequiredService<PluginManager>();
        var metrics = app.Services.GetRequiredService<PrometheusMetrics>();
        app.Services.GetRequiredService<IPluginEventBus>().Subscribe<PluginOperationEvent>(operation =>
        {
            metrics.RecordPluginOperation(operation.PluginId, operation.Operation, operation.Success);
            return Task.CompletedTask;
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await pluginManager.InitializePluginsAsync(app.Services);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "插件系统初始化失败");
            }
        });

        startupSw.Restart();
        // 初始化认证服务（加载或创建 .lywaf.auth.json）
        var authService = app.Services.GetRequiredService<LyWaf.Services.Auth.IAuthService>();
        authService.Initialize();

        // 初始化 WAF 自定义规则服务（加载 .lywaf.rules.json）
        var wafRuleService = app.Services.GetRequiredService<IWafRuleService>();
        wafRuleService.Initialize();

        // 注册控制台 API
        app.MapControlApi(wafInfos);
        app.MapLyWafPluginApi(wafInfos.GetControlListen().Port);
        app.MapLyWafDistributionApi(wafInfos.GetControlListen().Port);
        app.MapLyWafRuntimeStateApi(wafInfos.GetControlListen().Port);
        app.MapLyWafResponseCacheApi(wafInfos.GetControlListen().Port);
        app.MapLyWafMetrics(wafInfos.GetControlListen().Port);

        var shutdownCoordinator = app.Services.GetRequiredService<LyWaf.Services.Lifecycle.GracefulShutdownCoordinator>();
        app.Lifetime.ApplicationStarted.Register(shutdownCoordinator.MarkReady);
        app.Lifetime.ApplicationStopping.Register(shutdownCoordinator.BeginDrain);

        // 启用 WebSocket 支持（使 context.WebSockets.IsWebSocketRequest 可用）
        app.UseWebSockets();

        // 配置全局插件中间件
        app.UseLyWafPlugins();

        // ACME HTTP-01 挑战中间件（必须在 HTTPS 重定向之前）
        app.UseAcmeChallenge();

        // 反向代理仅处理非 ControlListen 端口的请求
        var proxyPorts = wafInfos.Listens.Select(l => $"*:{l.Port}").ToArray();
        
        app.MapReverseProxy(proxyApp =>
        {
            proxyApp.UseMiddleware<LyWaf.Services.Sites.SiteStatisticsMiddleware>();
            proxyApp.UseMiddleware<LyWaf.Services.Incidents.SecurityIncidentMiddleware>();
            proxyApp.UseMiddleware<LyWaf.Services.Lifecycle.RequestDrainMiddleware>();
            proxyApp.UseMiddleware<TelemetryMiddleware>();
            proxyApp.UseMiddleware<MetricsMiddleware>();
            // 精确统计入站/出站字节（必须放在最外层）
            proxyApp.UseMiddleware<BandwidthCountingMiddleware>();
            // 路由级 CORS 及 OPTIONS 预检
            proxyApp.UseMiddleware<CorsMiddleware>();
            // 启用响应压缩（必须放在其他中间件之前）
            proxyApp.UseMiddleware<ResponseCompressMiddleware>();
            // 缓存未压缩且经过内层安全响应处理的最终内容
            proxyApp.UseMiddleware<ResponseCacheMiddleware>();
            // 在缓存写入前检查或脱敏上游响应，外层压缩随后处理最终内容。
            proxyApp.UseMiddleware<LyWaf.Services.ResponseWaf.ResponseWafMiddleware>();
            // 高优先级插件（Highest, High）- 在核心中间件之前
            proxyApp.UseLyWafPluginsInProxyHigh();
            
            // IP访问控制（含黑白名单规则、IP/地理限制、连接限制）
            proxyApp.UseMiddleware<AccessControlMiddleware>();
            // IP 请求日志记录（在访问控制之后，WAF 之前捕获完整请求）
            proxyApp.UseMiddleware<IpLogMiddleware>();
            // 自动 HTTPS 重定向
            proxyApp.UseMiddleware<AutoHttpsMiddleware>();
            proxyApp.UseMiddleware<WafControlMiddleware>();
            proxyApp.UseMiddleware<LyWaf.Services.AntiBot.AntiBotMiddleware>();
            proxyApp.UseMiddleware<CaptchaMiddleware>();
            proxyApp.UseLyWafPluginStage(PluginProxyStage.RequestProtection);
            proxyApp.UseMiddleware<ThrottledMiddleware>();
            proxyApp.UseMiddleware<SpeedLimitMiddleware>();
            proxyApp.UseLyWafPluginStage(PluginProxyStage.ResponseMonitoring);
            
            // 普通及低优先级插件（Normal, Low, Lowest）- 在核心中间件之后
            proxyApp.UseLyWafPluginsInProxyNormal();

            proxyApp.UseMiddleware<ExternalAuthorizationMiddleware>();
            // 仅复制通过安全检查且真正进入回源分支的请求
            proxyApp.UseMiddleware<RequestMirrorMiddleware>();
            
            // SimpleRes 简单响应处理中间件
            proxyApp.UseMiddleware<SimpleResMiddleware>();
            proxyApp.UseMiddleware<FileProviderMiddleware>();
            
            // 启用负载均衡中间件（使用自定义代理管道时必须显式添加）
            proxyApp.UseLoadBalancing();
            proxyApp.UsePassiveHealthChecks();
        }).RequireHost(proxyPorts);

        // 注册应用关闭时的清理
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            _logger.Info("应用正在关闭，等待托管服务停止...");
            // 记录进程关闭到运行日志
            try
            {
                var runtimeLog = app.Services.GetService<LyWaf.Services.DuckDb.RuntimeLogService>();
                runtimeLog?.RecordShutdown();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "记录运行日志关闭失败");
            }
        });
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            _logger.Info("托管服务已停止，释放静态资源...");
            SharedData.DisposeAll();
        });

        _logger.Info("[启动耗时] 中间件配置: {}ms", startupSw.ElapsedMilliseconds);
        startupSw.Restart();
        // app.Run() 会阻塞直到关机，无法用它后面的日志测启动耗时。
        // 在 ApplicationStarted 回调里记录"启动到就绪"的真实耗时。
        app.Lifetime.ApplicationStarted.Register(() =>
            _logger.Info("[启动耗时] 服务就绪(到 ApplicationStarted): {}ms", startupSw.ElapsedMilliseconds));
        _logger.Info("[启动耗时] 开始 app.Run()...");
        app.Run();
        _logger.Info("[启动耗时] app.Run() 退出");
    }
}
