using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;

namespace LyWaf.Utils;

public static class LogUtil
{
    public static void ConfigureNLog(string projectRoot, string? accessLog, string? errorLog, bool perfLog)
    {
        var config = new LoggingConfiguration();

        var consoleTarget = new ColoredConsoleTarget("console")
        {
            Layout = @"${date:format=HH\:mm\:ss.fff} ${level:padding=-5} ${logger:shortName=true}: ${message} ${exception:format=shortType,message}"
        };

        consoleTarget.RowHighlightingRules.Add(
            new ConsoleRowHighlightingRule("level == LogLevel.Error",
                ConsoleOutputColor.Red, ConsoleOutputColor.Black));
        consoleTarget.RowHighlightingRules.Add(
            new ConsoleRowHighlightingRule("level == LogLevel.Warn",
                ConsoleOutputColor.Yellow, ConsoleOutputColor.Black));

        config.AddTarget(consoleTarget);

        accessLog ??= "ly_${shortdate}.log";
        errorLog ??= "ly_${shortdate}_err.log";
        accessLog = accessLog.Replace("#", "$");
        errorLog = errorLog.Replace("#", "$");
        var fileTarget = new FileTarget("file")
        {
            FileName = Path.Combine(projectRoot, "logs", accessLog),
            ArchiveFileName = Path.Combine(projectRoot, "logs", "archive", "{#}.log"),
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveFiles = 30,
            Layout = "${longdate} ${level:uppercase=true} ${logger} ${message} ${exception:format=tostring}",
            KeepFileOpen = perfLog,  // 非独占模式，每次写入后关闭文件
            Encoding = System.Text.Encoding.UTF8
        };
        config.AddTarget(fileTarget);

        var errorFileTarget = new FileTarget("errorFile")
        {
            FileName = Path.Combine(projectRoot, "logs", errorLog),
            Layout = "${longdate} ${level:uppercase=true} ${logger} ${message} ${exception:format=tostring}",
            KeepFileOpen = perfLog,  // 非独占模式，每次写入后关闭文件
        };
        config.AddTarget(errorFileTarget);

        // 添加规则
        config.AddRuleForOneLevel(NLog.LogLevel.Error, errorFileTarget);
        config.AddRuleForAllLevels(consoleTarget);
        config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, fileTarget);

        // 应用配置
        LogManager.Configuration = config;
    }
}