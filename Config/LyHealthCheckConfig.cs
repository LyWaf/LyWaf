using System.Globalization;
using System.Text.RegularExpressions;
using LyWaf.Policy;

namespace LyWaf.Config;

internal static class LyHealthCheckConfig
{
    public static void Apply(object? value, Dictionary<string, object> cluster)
    {
        if (value is not Dictionary<string, object> source)
            throw new LyConfigException("health_check 必须为配置块");
        if (source.Keys.Any(k => k.Equals("Active", StringComparison.OrdinalIgnoreCase) || k.Equals("Passive", StringComparison.OrdinalIgnoreCase)))
        {
            if (source.Keys.Any(k => !k.Equals("Active", StringComparison.OrdinalIgnoreCase) && !k.Equals("Passive", StringComparison.OrdinalIgnoreCase) && !k.Equals("AvailableDestinationsPolicy", StringComparison.OrdinalIgnoreCase)))
                throw new LyConfigException("health_check 不可混用原生配置和简写");
            cluster["HealthCheck"] = source;
            return;
        }
        var active = new Dictionary<string, object> { ["Enabled"] = true, ["Policy"] = "LyxActiveHealth" };
        var metadata = cluster.TryGetValue("Metadata", out var existing) && existing is Dictionary<string, object> map ? map : new();
        foreach (var (key, input) in source)
        {
            var text = Convert.ToString(input, CultureInfo.InvariantCulture) ?? "";
            switch (key.ToLowerInvariant())
            {
                case "enabled":
                    if (!bool.TryParse(text, out var enabled)) throw new LyConfigException("health_check.enabled 必须为布尔值");
                    active["Enabled"] = enabled; break;
                case "path":
                    if (!text.StartsWith('/') || text.IndexOfAny(['\r', '\n', '?', '#']) >= 0 || text.Length > 2048)
                        throw new LyConfigException("health_check.path 必须为以 / 开头的路径");
                    active["Path"] = text; break;
                case "method":
                    text = text.ToUpperInvariant();
                    if (!new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" }.Contains(text))
                        throw new LyConfigException("health_check.method 无效");
                    metadata["LyxActiveHealth.Method"] = text; break;
                case "body":
                    if (text.Length > 65536) throw new LyConfigException("health_check.body 超过 64 Ki 字符");
                    metadata["LyxActiveHealth.Body"] = text; break;
                case "content_type":
                    if (!new[] { "application/json", "application/x-www-form-urlencoded", "text/plain" }.Contains(text))
                        throw new LyConfigException("health_check.content_type 无效");
                    metadata["LyxActiveHealth.ContentType"] = text; break;
                case "status_codes":
                    if (input is System.Collections.IEnumerable items && input is not string)
                        text = string.Join(',', items.Cast<object>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim()));
                    text = string.Join(',', text.Split(',').Select(v => v.Trim()));
                    if (!Regex.IsMatch(text, HealthStatusCodes.Pattern)) throw new LyConfigException("health_check.status_codes 必须为三位状态码或 x 通配符");
                    metadata["LyxActiveHealth.AvalidCode"] = text; break;
                case "interval": case "timeout": case "passes": case "fails":
                    var maximum = key.ToLowerInvariant() switch { "interval" => 3600, "timeout" => 300, _ => 100 };
                    if (!int.TryParse(text, out var number) || number < 1 || number > maximum)
                        throw new LyConfigException($"health_check.{key} 必须为 1 至 {maximum} 的整数");
                    if (key.Equals("interval", StringComparison.OrdinalIgnoreCase)) active["Interval"] = TimeSpan.FromSeconds(number).ToString("c");
                    else if (key.Equals("timeout", StringComparison.OrdinalIgnoreCase)) active["Timeout"] = TimeSpan.FromSeconds(number).ToString("c");
                    else metadata["LyxActiveHealth." + (key.Equals("passes", StringComparison.OrdinalIgnoreCase) ? "Passes" : "Fails")] = text;
                    break;
                default: throw new LyConfigException($"未知 health_check 配置项: {key}");
            }
        }
        cluster["HealthCheck"] = new Dictionary<string, object> { ["Active"] = active };
        cluster["Metadata"] = metadata;
    }
}
