using System.Diagnostics;
using System.Text;
using LyWaf.Config;
using LyWaf.Plugins.Core;
using LyWaf.Services.Configuration;
using LyWaf.Services.Param;
using LyWaf.Services;
using LyWaf.Services.WafInfo;
using LyWaf.Shared;
using Microsoft.Extensions.Options;
using NLog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HashSet<string> AllowedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileProvider", "Logging", "Protect", "ReverseProxy", 
        "SpeedLimit", "Statistic", "WafInfos", "RuntimeState", "Distribution", "ServiceDiscovery"
    };
    
    // 缓存前端静态文件
    private static string? _indexHtmlCache;
    private static DateTime _indexHtmlLastModified;
    
    /// <summary>
    /// 获取前端静态文件目录
    /// </summary>
    private static string GetFrontendDistPath()
    {
        // 首先尝试 control_html 目录（构建后输出目录）
        var controlHtmlPath = Path.Combine(Directory.GetCurrentDirectory(), "control_html");
        if (Directory.Exists(controlHtmlPath)) return controlHtmlPath;
        
        // 然后尝试 Frontend/dist 目录（开发环境）
        var devPath = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "dist");
        if (Directory.Exists(devPath)) return devPath;
        
        // 然后尝试 BaseDirectory 下的 control_html（发布环境）
        var releasePath = Path.Combine(AppContext.BaseDirectory, "control_html");
        if (Directory.Exists(releasePath)) return releasePath;
        
        // 最后返回 BaseDirectory 下的 frontend
        return Path.Combine(AppContext.BaseDirectory, "frontend");
    }
    
    /// <summary>
    /// 获取 index.html 内容
    /// </summary>
    private static string GetIndexHtml()
    {
        var distPath = GetFrontendDistPath();
        var indexPath = Path.Combine(distPath, "index.html");
        
        if (!File.Exists(indexPath))
        {
            return @"<!DOCTYPE html>
<html><head><meta charset=""UTF-8""><title>LyWaf 控制台</title></head>
<body><h1>前端文件未找到</h1><p>请先构建前端: cd Frontend && npm run build</p></body></html>";
        }
        
        var lastModified = File.GetLastWriteTime(indexPath);
        if (_indexHtmlCache == null || lastModified > _indexHtmlLastModified)
        {
            _indexHtmlCache = File.ReadAllText(indexPath, Encoding.UTF8);
            _indexHtmlLastModified = lastModified;
        }
        
        return _indexHtmlCache;
    }
    

    private record PcapLogDeleteRequest(string? File);

    /// <summary>
    /// 递归展平 IConfigurationSection 为 flat key-value
    /// </summary>
    private static void FlattenConfigSection(IConfigurationSection section, string prefix, Dictionary<string, string> result)
    {
        if (section.Value != null)
        {
            result[prefix] = section.Value;
        }

        foreach (var child in section.GetChildren())
        {
            FlattenConfigSection(child, $"{prefix}:{child.Key}", result);
        }
    }

    /// <summary>
    /// 清空对象上所有 List/Dictionary 属性，防止 Bind() 追加导致重复
    /// </summary>
    private static void ClearCollectionProperties(object obj)
    {
        if (obj == null) return;
        foreach (var prop in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            object? value;
            try { value = prop.GetValue(obj); } catch { continue; }
            if (value is System.Collections.IList list)
                list.Clear();
            else if (value is System.Collections.IDictionary dict)
                dict.Clear();
        }
    }

    /// <summary>
    /// 将 Options 对象实例的公共属性序列化为 flat key-value（支持嵌套对象、列表、字典）
    /// 跳过仅有 setter 的别名属性（如 ConfigurationKeyName 别名）
    /// </summary>
    private static void SerializeOptionsToFlat(object obj, string prefix, Dictionary<string, string> result)
    {
        if (obj == null) return;
        var type = obj.GetType();

        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            // 跳过只有 setter 没有 getter 的属性（别名属性）
            if (!prop.CanRead) continue;
            // 跳过 nullable setter-only 别名（getter 返回 null 的可空别名属性）
            if (prop.PropertyType.IsGenericType &&
                prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                !prop.CanWrite) continue;

            // 跳过 set-only 别名属性（只有 set 没有真实 get）
            var getter = prop.GetGetMethod();
            if (getter == null) continue;

            object? value;
            try { value = prop.GetValue(obj); }
            catch { continue; }

            // 通过 ConfigurationKeyName 获取配置键名
            var keyAttr = prop.GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute), false)
                .FirstOrDefault() as Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute;
            var key = keyAttr?.Name ?? prop.Name;

            // 跳过别名属性（属性类型是 Nullable，且名称以 Alias 结尾）
            if (prop.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;

            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

            if (value == null)
            {
                continue;
            }
            else if (value is string s)
            {
                result[fullKey] = s;
            }
            else if (value is bool b)
            {
                result[fullKey] = b ? "true" : "false";
            }
            else if (value is int or long or float or double or decimal)
            {
                result[fullKey] = value.ToString() ?? "";
            }
            else if (value is System.Collections.IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item is string || item?.GetType().IsPrimitive == true)
                    {
                        result[$"{fullKey}:{i}"] = item?.ToString() ?? "";
                    }
                    else if (item != null)
                    {
                        SerializeOptionsToFlat(item, $"{fullKey}:{i}", result);
                    }
                }
            }
            else if (value is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var entryKey = entry.Key?.ToString() ?? "";
                    var entryVal = entry.Value;
                    if (entryVal is string || entryVal?.GetType().IsPrimitive == true)
                    {
                        result[$"{fullKey}:{entryKey}"] = entryVal?.ToString() ?? "";
                    }
                    else if (entryVal != null)
                    {
                        SerializeOptionsToFlat(entryVal, $"{fullKey}:{entryKey}", result);
                    }
                }
            }
            else if (value.GetType().IsClass)
            {
                SerializeOptionsToFlat(value, fullKey, result);
            }
            else
            {
                result[fullKey] = value.ToString() ?? "";
            }
        }
    }

    /// <summary>
    /// 从 Options 类型提取 [Description] 属性标签，用于前端显示中文名称
    /// </summary>
    private static Dictionary<string, string> ExtractPropertyLabels(Type type, string prefix = "")
    {
        var labels = new Dictionary<string, string>();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            // 跳过别名属性
            if (prop.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;

            var keyAttr = prop.GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute), false)
                .FirstOrDefault() as Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute;
            var key = keyAttr?.Name ?? prop.Name;
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

            var descAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
            if (descAttr != null && !string.IsNullOrEmpty(descAttr.Description))
            {
                labels[fullKey] = descAttr.Description;
            }

            // 递归处理嵌套对象（非集合、非基元、非字符串）
            var propType = prop.PropertyType;
            if (propType.IsClass && propType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propType)
                && !propType.IsGenericType)
            {
                foreach (var kv in ExtractPropertyLabels(propType, fullKey))
                    labels[kv.Key] = kv.Value;
            }

            // List<T> 中 T 是带 [Description] 的对象类型时，提取 T 的属性标签
            if (propType.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(propType))
            {
                var elementType = propType.GetGenericArguments()[0];
                if (elementType.IsClass && elementType != typeof(string))
                {
                    foreach (var kv in ExtractPropertyLabels(elementType, fullKey))
                        labels[kv.Key] = kv.Value;
                }
            }
        }
        return labels;
    }

    /// <summary>
    /// 提取 List&lt;T&gt; 元素类型的属性默认值
    /// 例如 List&lt;MatchRule&gt; → { "MatchRules:LogHeaders": "false", "MatchRules:LogDuration": "true", ... }
    /// 用于前端推断字段类型（bool/string/int）和初始值
    /// </summary>
    private static Dictionary<string, string> ExtractListElementDefaults(Type type, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            if (prop.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;

            var keyAttr = prop.GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute), false)
                .FirstOrDefault() as Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute;
            var key = keyAttr?.Name ?? prop.Name;
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

            var propType = prop.PropertyType;

            // List<T> where T is a complex class
            if (propType.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(propType))
            {
                var elementType = propType.GetGenericArguments()[0];
                if (elementType.IsClass && elementType != typeof(string))
                {
                    // 创建一个 T 实例，序列化其默认值
                    try
                    {
                        var defaultInstance = Activator.CreateInstance(elementType);
                        if (defaultInstance != null)
                        {
                            SerializeOptionsToFlat(defaultInstance, fullKey, result);
                        }
                    }
                    catch { /* 无法创建实例则跳过 */ }
                }
            }

            // 递归处理嵌套对象
            if (propType.IsClass && propType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propType)
                && !propType.IsGenericType)
            {
                foreach (var kv in ExtractListElementDefaults(propType, fullKey))
                    result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// 将 flat key-value (如 "MaxWidth" → "2048", "SupportedFormats:0" → "jpg") 转为嵌套字典
    /// </summary>
    private static Dictionary<string, object> FlatKeysToNestedDict(Dictionary<string, string> flat)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in flat)
        {
            var parts = key.Split(':');
            SetNestedValue(result, parts, 0, value);
        }

        // 将纯数字 key 的字典转为数组
        return ConvertIndexedDictsToArrays(result);
    }

    private static void SetNestedValue(Dictionary<string, object> dict, string[] parts, int index, string value)
    {
        if (index == parts.Length - 1)
        {
            dict[parts[index]] = value;
            return;
        }

        var key = parts[index];
        if (!dict.TryGetValue(key, out var existing) || existing is not Dictionary<string, object> nested)
        {
            nested = new Dictionary<string, object>();
            dict[key] = nested;
        }
        SetNestedValue(nested, parts, index + 1, value);
    }

    /// <summary>
    /// 如果一个字典的所有 key 都是连续数字 (0, 1, 2, ...)，则转成 List
    /// </summary>
    private static Dictionary<string, object> ConvertIndexedDictsToArrays(Dictionary<string, object> dict)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, val) in dict)
        {
            if (val is Dictionary<string, object> nested)
            {
                var converted = ConvertIndexedDictsToArrays(nested);
                // 检查是否所有 key 是数字索引
                if (converted.Count > 0 && converted.Keys.All(k => int.TryParse(k, out _)))
                {
                    var list = converted.OrderBy(kv => int.Parse(kv.Key)).Select(kv => kv.Value).ToList();
                    result[key] = list;
                }
                else
                {
                    result[key] = converted;
                }
            }
            else
            {
                result[key] = val;
            }
        }
        return result;
    }

    /// <summary>
    /// 修改补丁文件中的 PluginConfigs 节点
    /// </summary>
    private static async Task<IResult> ModifyPluginConfigPatch(IConfiguration config, string pluginId,
        Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var pluginConfigs = EnsurePatchSection(patch, "PluginConfigs");

            var error = modifier(pluginConfigs);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "插件配置已更新" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    // =============== 配置来源检测辅助方法 ===============

    /// <summary>
    /// 配置来源枚举
    /// </summary>
    private enum ConfigSource
    {
        OriginalConfig,  // 原始配置文件 (.ly 或 .yaml)
        PatchConfig,     // 补丁配置文件 (.lywaf.lb-patch.json)
        NotFound         // 配置不存在
    }

    /// <summary>
    /// 检测指定配置项的来源
    /// </summary>
    /// <param name="config">配置对象</param>
    /// <param name="key">配置键</param>
    /// <returns>配置来源</returns>
    private static ConfigSource DetectConfigSource(IConfiguration config, string key)
    {
        // 检查是否存在于补丁配置中
        var patchPath = LbPatchConfig.GetPatchFilePath();
        if (File.Exists(patchPath))
        {
            try
            {
                var json = File.ReadAllText(patchPath, Encoding.UTF8);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                
                // 将配置键转换为补丁文件中的路径
                var patchKey = key;
                if (patchKey.StartsWith("ReverseProxy:"))
                {
                    patchKey = patchKey.Substring("ReverseProxy:".Length);
                }
                else if (patchKey.StartsWith("WafInfos:Listens:"))
                {
                    patchKey = patchKey.Substring("WafInfos:".Length);
                }
                
                if (FindValueInJsonElement(doc.RootElement, patchKey))
                {
                    return ConfigSource.PatchConfig;
                }
            }
            catch (Exception)
            {
                // 补丁文件读取失败，继续检查原始配置
            }
        }
        
        // 检查是否存在于原始配置中
        var configPath = SharedData.ConfigFilePath;
        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath, Encoding.UTF8);
                
                if (configPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    configPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    // YAML 文件解析
                    using var reader = new StringReader(content);
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .Build();
                    var yamlObj = deserializer.Deserialize(reader);
                    if (yamlObj != null && FindValueInYamlObject(yamlObj, key))
                    {
                        return ConfigSource.OriginalConfig;
                    }
                }
                else if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    // JSON 文件解析
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    if (FindValueInJsonElement(doc.RootElement, key))
                    {
                        return ConfigSource.OriginalConfig;
                    }
                }
            }
            catch (Exception)
            {
                // 原始配置文件读取失败
            }
        }
        
        return ConfigSource.NotFound;
    }

    /// <summary>
    /// 在 JsonElement 中查找指定的键路径
    /// </summary>
    private static bool FindValueInJsonElement(System.Text.Json.JsonElement element, string keyPath)
    {
        var parts = keyPath.Split(':');
        var current = element;
        
        foreach (var part in parts)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
                
            if (!current.TryGetProperty(part, out current))
                return false;
        }
        
        return true;
    }

    /// <summary>
    /// 在 YAML 对象中查找指定的键路径
    /// </summary>
    private static bool FindValueInYamlObject(object yamlObj, string keyPath)
    {
        var parts = keyPath.Split(':');
        var current = yamlObj;
        
        foreach (var part in parts)
        {
            if (current is not Dictionary<object, object> dict)
                return false;
                
            if (!dict.TryGetValue(part, out current))
                return false;
        }
        
        return current != null;
    }

    // =============== 补丁文件辅助方法 ===============

    private static readonly SemaphoreSlim _lbPatchLock = new(1, 1);

    /// <summary>
    /// 读取补丁文件并反序列化为可操作的字典
    /// </summary>
    private static async Task<Dictionary<string, object>> ReadPatchFile()
    {
        var patchPath = LbPatchConfig.GetPatchFilePath();
        Dictionary<string, object> patch;
        if (File.Exists(patchPath))
        {
            var json = await File.ReadAllTextAsync(patchPath, Encoding.UTF8);
            try
            {
                patch = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }) ?? new();
            }
            catch (System.Exception)
            {
                patch = new Dictionary<string, object>();
            }
            }
        else
        {
            patch = new Dictionary<string, object>();
        }
        return patch;
    }

    /// <summary>
    /// 确保补丁中指定节点存在，并返回可操作的字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchSection(Dictionary<string, object> patch, string sectionName)
    {
        if (!patch.ContainsKey(sectionName))
            patch[sectionName] = new Dictionary<string, object>();

        var sectionObj = patch[sectionName];
        Dictionary<string, object> section;
        if (sectionObj is System.Text.Json.JsonElement je)
        {
            section = JsonElementToDict(je);
        }
        else if (sectionObj is Dictionary<string, object> dict)
        {
            section = dict;
        }
        else
        {
            section = new Dictionary<string, object>();
        }
        patch[sectionName] = section;
        return section;
    }

    /// <summary>
    /// 更新补丁中的源文件跟踪信息
    /// </summary>
    private static void UpdatePatchSourceTracking(Dictionary<string, object> patch)
    {
        var configPath = SharedData.ConfigFilePath;
        if (File.Exists(configPath))
        {
            var lastModified = File.GetLastWriteTimeUtc(configPath);
            patch["_source"] = new Dictionary<string, object>
            {
                ["file"] = Path.GetFileName(configPath),
                ["lastModified"] = lastModified.ToString("o")
            };
        }
    }

    /// <summary>响应发送完成后开始摘流和排空，最终触发 Host 的有序停止流程。</summary>
    private static void ScheduleDrainAndStop(HttpContext context, IHostApplicationLifetime lifetime, IConfiguration config)
    {
        var coordinator = context.RequestServices.GetRequiredService<LyWaf.Services.Lifecycle.GracefulShutdownCoordinator>();
        var timeoutSeconds = Math.Clamp(config.GetValue("Lifecycle:DrainTimeoutSeconds", 30), 1, 600);
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            coordinator.BeginDrain();
            var drained = await coordinator.WaitForDrainAsync(TimeSpan.FromSeconds(timeoutSeconds));
            if (!drained)
            {
                var state = coordinator.Snapshot();
                Logger.Warn("请求排空超时，继续停止。ActiveRequests={Requests}, ActiveWebSockets={WebSockets}", state.ActiveRequests, state.ActiveWebSockets);
            }
            lifetime.StopApplication();
        });

    }

    /// <summary>取得可在新进程中重新启动当前版本的入口点。</summary>
    private static string GetRunningEntryPoint()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && Path.GetFileNameWithoutExtension(processPath).Equals("LyWaf", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(processPath);
        return Path.GetFullPath(System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("无法确定当前程序入口点"));
    }

    /// <summary>重载并验证刚写入的版本；失败时恢复写入前版本，确保运行配置和磁盘配置一致。</summary>
    private static async Task<ConfigurationChangeImpact> ApplyVersionReloadOrRollback(
        ConfigVersionInfo version,
        IConfigurationRoot root,
        IReadOnlyDictionary<string, string?> before)
    {
        var lifecycle = ServiceLocator.GetRequiredService<ConfigurationLifecycleService>()
            ?? throw new InvalidOperationException("配置生命周期服务尚未初始化");
        try
        {
            return lifecycle.ReloadAndValidate(root, before);
        }
        catch
        {
            if (version.PreviousVersionId != null)
            {
                var versions = ServiceLocator.GetRequiredService<ConfigVersionService>()
                    ?? throw new InvalidOperationException("配置版本服务尚未初始化");
                await versions.RollbackAsync(version.PreviousVersionId);
                root.Reload();
            }
            throw;
        }
    }

    /// <summary>
    /// 保存补丁文件并触发配置重载
    /// </summary>
    private static async Task SavePatchAndReload(Dictionary<string, object> patch, IConfiguration config)
    {
        var patchPath = LbPatchConfig.GetPatchFilePath();
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var newJson = System.Text.Json.JsonSerializer.Serialize(patch, options);
        var versionService = ServiceLocator.GetRequiredService<ConfigVersionService>()
            ?? throw new InvalidOperationException("配置版本服务尚未初始化");
        var version = await versionService.WritePatchAsync(patchPath, newJson, "patch-update");

        if (config is IConfigurationRoot configRoot)
        {
            var lifecycle = ServiceLocator.GetRequiredService<ConfigurationLifecycleService>()
                ?? throw new InvalidOperationException("配置生命周期服务尚未初始化");
            await ApplyVersionReloadOrRollback(version, configRoot, lifecycle.Capture(configRoot));
        }
    }

    /// <summary>
    /// 修改集群补丁并触发配置重载
    /// modifier 接收 Clusters 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyLbPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var clusters = EnsurePatchSection(patch, "Clusters");

            var error = modifier(clusters);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改路由补丁并触发配置重载
    /// modifier 接收 Routes 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyRoutePatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var routes = EnsurePatchSection(patch, "Routes");

            var error = modifier(routes);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 FileServer 节点
    /// modifier 接收 FileServer 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyFileServerPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var fileservers = EnsurePatchSection(patch, "FileServer");

            var error = modifier(fileservers);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 SimpleRes 节点
    /// </summary>
    private static async Task<IResult> ModifySimpleResPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var simpleres = EnsurePatchSection(patch, "SimpleRes");

            var error = modifier(simpleres);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 Listens 节点（监听端口）
    /// modifier 接收 Listens 字典（key 为索引），返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyListenPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier,
        bool restartRequired = true)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var listens = EnsurePatchSection(patch, "Listens");

            var error = modifier(listens);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);

            if (restartRequired)
            {
                return Results.Json(new { success = true, message = "监听端口已更新，需要重启服务才能生效", restartRequired = true });
            }
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改原始配置文件并触发配置重载
    /// modifier 返回 null 表示成功，返回字符串表示错误信息
    /// </summary>
    private static async Task<IResult> ModifyOriginalConfig(IConfiguration config, string clusterId, Func<object, string?> modifier)
    {
        var configPath = SharedData.ConfigFilePath;
        if (!File.Exists(configPath))
        {
            return Results.Json(new { success = false, message = "配置文件不存在" }, statusCode: 500);
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
            object configObj;
            
            if (configPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                configPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                // YAML 文件处理
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                var serializer = new SerializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
                    .Build();
                    
                using var reader = new StringReader(content);
                configObj = deserializer.Deserialize(reader) ?? new Dictionary<string, object>();
                
                // 执行修改
                var error = modifier(configObj);
                if (error != null)
                {
                    return Results.Json(new { success = false, message = error }, statusCode: 400);
                }
                
                // 保存 YAML 文件
                var newYaml = serializer.Serialize(configObj);
                var versionService = ServiceLocator.GetRequiredService<ConfigVersionService>()
                    ?? throw new InvalidOperationException("配置版本服务尚未初始化");
                var version = await versionService.WriteMainAsync(configPath, newYaml, $"update-cluster:{clusterId}");
                if (config is IConfigurationRoot yamlRoot)
                {
                    var lifecycle = ServiceLocator.GetRequiredService<ConfigurationLifecycleService>()
                        ?? throw new InvalidOperationException("配置生命周期服务尚未初始化");
                    await ApplyVersionReloadOrRollback(version, yamlRoot, lifecycle.Capture(yamlRoot));
                }
            }
            else if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // JSON 文件处理
                var jsonOptions = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = false 
                };
                
                configObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content) 
                           ?? new Dictionary<string, object>();
                
                // 执行修改
                var error = modifier(configObj);
                if (error != null)
                {
                    return Results.Json(new { success = false, message = error }, statusCode: 400);
                }
                
                // 保存 JSON 文件
                var newJson = System.Text.Json.JsonSerializer.Serialize(configObj, jsonOptions);
                var versionService = ServiceLocator.GetRequiredService<ConfigVersionService>()
                    ?? throw new InvalidOperationException("配置版本服务尚未初始化");
                var version = await versionService.WriteMainAsync(configPath, newJson, $"update-cluster:{clusterId}");
                if (config is IConfigurationRoot jsonRoot)
                {
                    var lifecycle = ServiceLocator.GetRequiredService<ConfigurationLifecycleService>()
                        ?? throw new InvalidOperationException("配置生命周期服务尚未初始化");
                    await ApplyVersionReloadOrRollback(version, jsonRoot, lifecycle.Capture(jsonRoot));
                }
            }
            else
            {
                return Results.Json(new { success = false, message = "不支持的配置文件格式" }, statusCode: 500);
            }

            return Results.Json(new { success = true, message = "原始配置已更新并重载" });
        }
        catch (Exception ex)
        {
            return Results.Json(new { success = false, message = $"修改原始配置失败: {ex.Message}" }, statusCode: 500);
        }
    }

    /// <summary>
    /// 确保补丁中存在指定集群节点，返回该集群字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchCluster(Dictionary<string, object> clusters, string clusterId)
    {
        if (clusters.TryGetValue(clusterId, out var clusterObj))
        {
            if (clusterObj is System.Text.Json.JsonElement je)
            {
                var dict = JsonElementToDict(je);
                clusters[clusterId] = dict;
                return dict;
            }
            if (clusterObj is Dictionary<string, object> existing)
                return existing;
        }
        var newCluster = new Dictionary<string, object>();
        clusters[clusterId] = newCluster;
        return newCluster;
    }

    /// <summary>
    /// 确保补丁集群字典中存在 Destinations 子字典，不存在则创建空字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchDestinations(Dictionary<string, object> clusterPatch)
    {
        if (clusterPatch.TryGetValue("Destinations", out var destsObj))
        {
            if (destsObj is System.Text.Json.JsonElement je)
            {
                var dict = JsonElementToDict(je);
                clusterPatch["Destinations"] = dict;
                return dict;
            }
            if (destsObj is Dictionary<string, object> existing)
                return existing;
        }
        var newDests = new Dictionary<string, object>();
        clusterPatch["Destinations"] = newDests;
        return newDests;
    }

    /// <summary>
    /// [已废弃] 从 IConfiguration 快照当前集群的 Destinations 和 LoadBalancingPolicy 到补丁字典
    /// </summary>
    private static void SnapshotClusterDestinations(IConfiguration config, string clusterId, Dictionary<string, object> clusterPatch)
    {
        var clusterSection = config.GetSection($"ReverseProxy:Clusters:{clusterId}");

        // 保持策略
        if (!clusterPatch.ContainsKey("LoadBalancingPolicy"))
        {
            var policy = clusterSection["LoadBalancingPolicy"];
            if (policy != null) clusterPatch["LoadBalancingPolicy"] = policy;
        }

        // 快照 destinations（如果补丁中还没有）
        if (!clusterPatch.ContainsKey("Destinations"))
        {
            var dests = new Dictionary<string, object>();
            var destsSection = clusterSection.GetSection("Destinations");
            foreach (var dest in destsSection.GetChildren())
            {
                var destDict = new Dictionary<string, object>();
                var address = dest["Address"];
                if (address != null) destDict["Address"] = address;

                var metaSection = dest.GetSection("Metadata");
                var metaChildren = metaSection.GetChildren().ToList();
                if (metaChildren.Count > 0)
                {
                    var meta = new Dictionary<string, object>();
                    foreach (var m in metaChildren)
                    {
                        if (m.Value != null) meta[m.Key] = m.Value;
                    }
                    destDict["Metadata"] = meta;
                }

                dests[dest.Key] = destDict;
            }
            clusterPatch["Destinations"] = dests;
        }
    }

    /// <summary>
    /// 递归将 JsonElement 转换为 Dictionary
    /// </summary>
    private static Dictionary<string, object> JsonElementToDict(System.Text.Json.JsonElement element)
    {
        var dict = new Dictionary<string, object>();
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return dict;

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToValue(prop.Value);
        }
        return dict;
    }

    private static object JsonElementToValue(System.Text.Json.JsonElement value)
    {
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => JsonElementToDict(value),
            System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToValue).ToList<object>(),
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => value.ToString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    /// <summary>
    /// 将 JsonElement 转换为 YAML 兼容的字典（保留原生类型：bool/int/string/List/Dict）
    /// </summary>
    private static Dictionary<string, object> JsonElementToYamlDict(System.Text.Json.JsonElement element)
    {
        var dict = new Dictionary<string, object>();
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return dict;

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToYamlValue(prop.Value);
        }
        return dict;
    }

    private static object JsonElementToYamlValue(System.Text.Json.JsonElement value)
    {
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => JsonElementToYamlDict(value),
            System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToYamlValue).ToList(),
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => value.TryGetInt32(out var i) ? i : (object)value.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => value.ToString()
        };
    }

    /// <summary>
    /// 确保字典中存在指定 key 的子字典，不存在则创建
    /// </summary>
    private static Dictionary<string, object> EnsureNestedDict(Dictionary<string, object> parent, string key)
    {
        if (parent.TryGetValue(key, out var existing) && existing is Dictionary<string, object> dict)
            return dict;

        var newDict = new Dictionary<string, object>();
        parent[key] = newDict;
        return newDict;
    }

    /// <summary>
    /// 从路由 ID 中提取端口号，支持 listen_{port}_default / simpleres_listen_{port}_default 等格式
    /// </summary>
    private static int? ExtractPortFromRouteId(string routeId)
    {
        var listenIdx = routeId.IndexOf("listen_", StringComparison.Ordinal);
        if (listenIdx < 0 || !routeId.EndsWith("_default")) return null;
        var portStr = routeId[(listenIdx + "listen_".Length)..^"_default".Length];
        return int.TryParse(portStr, out var port) ? port : null;
    }

    private static object? GetSectionValue(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        
        if (children.Count == 0)
        {
            return section.Value;
        }
        
        // 检查是否是数组（子项的 Key 都是数字）
        if (children.All(c => int.TryParse(c.Key, out _)))
        {
            var list = new List<object?>();
            foreach (var child in children.OrderBy(c => int.Parse(c.Key)))
            {
                list.Add(GetSectionValue(child));
            }
            return list;
        }
        
        // 否则是对象
        var dict = new Dictionary<string, object?>();
        foreach (var child in children)
        {
            dict[child.Key] = GetSectionValue(child);
        }
        return dict;
    }

    /// 获取本机真实局域网 IP 地址
    /// 排除虚拟网卡（VMware、VirtualBox、Hyper-V、Docker 等）
    /// 优先选择有默认网关的物理/Wi-Fi 网卡上的 RFC 1918 私有地址
    /// </summary>
    private static string GetLocalNetworkIp(GlobalOptions? globalOptions = null)
    {
        // 优先使用用户配置的本地地址
        if (!string.IsNullOrWhiteSpace(globalOptions?.LocalAddress))
            return globalOptions.LocalAddress.Trim();

        try
        {
            var candidates = new List<(System.Net.IPAddress addr, int priority, bool hasGateway)>();
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in networkInterfaces)
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                // 排除虚拟网卡
                if (IsVirtualAdapter(ni)) continue;

                // 只保留以太网和 Wi-Fi
                if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet)
                    continue;

                var ipProps = ni.GetIPProperties();

                // 有默认网关 = 真正联网的接口
                var hasGateway = ipProps.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !g.Address.Equals(System.Net.IPAddress.Any));

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    var addr = unicast.Address;
                    if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                    var bytes = addr.GetAddressBytes();
                    var priority = GetPrivateAddressPriority(bytes);
                    if (priority >= 0)
                        candidates.Add((addr, priority, hasGateway));
                }
            }

            if (candidates.Count > 0)
            {
                // 有网关的优先，然后按私有地址优先级排序
                candidates.Sort((a, b) =>
                {
                    var gwCmp = b.hasGateway.CompareTo(a.hasGateway); // true 排前面
                    return gwCmp != 0 ? gwCmp : a.priority.CompareTo(b.priority);
                });
                return candidates[0].addr.ToString();
            }

            // 没有匹配的私有地址，用 UDP socket 探测出口 IP
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is System.Net.IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { }

        return "127.0.0.1";
    }

    /// <summary>
    /// 判断网卡是否为虚拟网卡（VMware、VirtualBox、Hyper-V、Docker、WSL 等）
    /// 通过 Description 和 Name 中的关键字匹配
    /// </summary>
    private static bool IsVirtualAdapter(System.Net.NetworkInformation.NetworkInterface ni)
    {
        var desc = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        string[] virtualKeywords = [
            "vmware", "vmnet", "virtualbox", "vbox",
            "hyper-v", "virtual",
            "docker", "veth", "br-",
            "wsl", "vpn", "tap-", "tun",
            "teredo", "isatap", "6to4",
            "bluetooth", "vnic", "virbr"
        ];

        foreach (var kw in virtualKeywords)
        {
            if (desc.Contains(kw) || name.Contains(kw))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 判断 IPv4 是否为 RFC 1918 私有地址，并返回优先级
    /// 192.168.x.x = 0（最高）, 10.x.x.x = 1, 172.16-31.x.x = 2
    /// 非私有地址返回 -1
    /// </summary>
    private static int GetPrivateAddressPriority(byte[] bytes)
    {
        if (bytes[0] == 192 && bytes[1] == 168) return 0;
        if (bytes[0] == 10) return 1;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return 2;
        return -1;
    }

}
