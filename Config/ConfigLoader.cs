using Microsoft.Extensions.Configuration;
using NLog;

namespace LyWaf.Config;

/// <summary>
/// 配置加载器
/// 支持 .ly 配置文件，自动转换为内部配置格式
/// </summary>
public static class ConfigLoader
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private const string LY_CONFIG_FILE = "config.ly";
    private const string GENERATED_YAML_FILE = ".lywaf.generated.yaml";

    /// <summary>
    /// 加载配置文件
    /// 优先加载 config.ly，如果存在则转换为 YAML 格式
    /// </summary>
    public static IConfigurationBuilder LoadLyConfig(this IConfigurationBuilder builder, string? configPath = null)
    {
        var lyConfigPath = configPath ?? LY_CONFIG_FILE;

        // 检查是否存在 .ly 配置文件
        if (File.Exists(lyConfigPath))
        {
            _logger.Info("发现 LyWaf 配置文件: {Path}", lyConfigPath);

            try
            {
                // 收集环境变量
                var variables = new Dictionary<string, string>();
                foreach (var key in Environment.GetEnvironmentVariables().Keys)
                {
                    var keyStr = key.ToString()!;
                    variables[keyStr] = Environment.GetEnvironmentVariable(keyStr) ?? "";
                }

                // 解析并转换配置
                var yamlContent = LyToAppSettingsConverter.Convert(
                    File.ReadAllText(lyConfigPath),
                    variables
                );

                // 保存生成的 YAML（用于调试）
                var generatedPath = Path.Combine(
                    Path.GetDirectoryName(lyConfigPath) ?? ".",
                    GENERATED_YAML_FILE
                );
                File.WriteAllText(generatedPath, yamlContent);
                _logger.Debug("生成的配置已保存到: {Path}", generatedPath);

                // 从生成的 YAML 加载配置
                builder.AddYamlFile(generatedPath, optional: true, reloadOnChange: false);

                _logger.Info("LyWaf 配置已加载");
            }
            catch (LyConfigException ex)
            {
                _logger.Error(ex, "解析配置文件失败: {Path}", lyConfigPath);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "加载配置文件失败: {Path}", lyConfigPath);
                throw;
            }
        }

        return builder;
    }

    /// <summary>
    /// 创建用于流式加载的 YAML 流
    /// </summary>
    public static Stream? CreateConfigStream(string lyConfigPath, Dictionary<string, string>? variables = null)
    {
        if (!File.Exists(lyConfigPath))
            return null;

        try
        {
            var content = File.ReadAllText(lyConfigPath);
            var yaml = LyToAppSettingsConverter.Convert(content, variables);
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(yaml));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "转换配置失败: {Path}", lyConfigPath);
            return null;
        }
    }
}

/// <summary>
/// LyConfig 配置源
/// </summary>
public class LyConfigSource : IConfigurationSource
{
    public string FilePath { get; set; } = "config.ly";
    public Dictionary<string, string> Variables { get; set; } = [];
    public bool Optional { get; set; } = true;

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new LyConfigProvider(this);
    }
}

/// <summary>
/// LyConfig 配置提供程序
/// </summary>
public class LyConfigProvider : ConfigurationProvider
{
    private readonly LyConfigSource _source;
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public LyConfigProvider(LyConfigSource source)
    {
        _source = source;
    }

    public override void Load()
    {
        if (!File.Exists(_source.FilePath))
        {
            if (!_source.Optional)
            {
                throw new FileNotFoundException($"配置文件不存在: {_source.FilePath}");
            }
            return;
        }

        try
        {
            var content = File.ReadAllText(_source.FilePath);
            var config = LyConfigParser.Parse(content, _source.Variables);
            
            // 转换为 appsettings 格式（处理 listen -> WafInfos 等）
            var appSettings = LyToAppSettingsConverter.TransformToAppSettings(config);
            
            FlattenDictionary(appSettings, "");
            _logger.Info("LyWaf 配置已加载: {Path}", _source.FilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载配置失败: {Path}", _source.FilePath);
            if (!_source.Optional)
                throw;
        }
    }

    private void FlattenDictionary(Dictionary<string, object> dict, string prefix)
    {
        foreach (var kv in dict)
        {
            var key = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}:{kv.Key}";
            FlattenValue(key, kv.Value);
        }
    }

    private void FlattenValue(string key, object? value)
    {
        switch (value)
        {
            case Dictionary<string, object> nestedDict:
                FlattenDictionary(nestedDict, key);
                break;

            case Dictionary<string, string> stringDict:
                foreach (var kv in stringDict)
                {
                    var itemKey = $"{key}:{kv.Key}";
                    Data[itemKey] = kv.Value ?? "";
                }
                break;

            case System.Collections.IDictionary genericDict:
                foreach (System.Collections.DictionaryEntry entry in genericDict)
                {
                    var itemKey = $"{key}:{entry.Key}";
                    FlattenValue(itemKey, entry.Value);
                }
                break;

            case List<object> list:
                for (var i = 0; i < list.Count; i++)
                {
                    var itemKey = $"{key}:{i}";
                    FlattenValue(itemKey, list[i]);
                }
                break;

            case System.Collections.IList genericList:
                var index = 0;
                foreach (var item in genericList)
                {
                    var itemKey = $"{key}:{index++}";
                    FlattenValue(itemKey, item);
                }
                break;

            default:
                Data[key] = value?.ToString() ?? "";
                break;
        }
    }
}

/// <summary>
/// 配置扩展方法
/// </summary>
public static class LyConfigExtensions
{
    /// <summary>
    /// 添加 LyConfig 配置源
    /// </summary>
    public static IConfigurationBuilder AddLyConfig(
        this IConfigurationBuilder builder,
        string filePath = "config.ly",
        bool optional = true,
        Dictionary<string, string>? variables = null)
    {
        return builder.Add(new LyConfigSource
        {
            FilePath = filePath,
            Optional = optional,
            Variables = variables ?? []
        });
    }
}
