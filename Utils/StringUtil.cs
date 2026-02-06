using System.Text;
using System.Text.RegularExpressions;

namespace LyWaf.Utils;

/// <summary>
/// 字符串工具类
/// </summary>
public static partial class StringUtil
{
    /// <summary>
    /// 占位符正则表达式（匹配 {xxx} 格式）
    /// </summary>
    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// 高效的模板字符串替换
    /// 替换 {placeholder} 格式的占位符
    /// </summary>
    /// <param name="template">模板字符串</param>
    /// <param name="values">占位符值字典（key 不包含大括号）</param>
    /// <param name="ignoreCase">是否忽略大小写，默认 true</param>
    /// <returns>替换后的字符串</returns>
    public static string ReplacePlaceholders(string template, Dictionary<string, string?> values, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(template) || values == null || values.Count == 0)
            return template;

        // 使用 Regex.Replace 一次遍历完成所有替换
        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;

            // 尝试精确匹配
            if (values.TryGetValue(key, out var value))
                return value ?? "";

            // 如果忽略大小写，尝试不区分大小写匹配
            if (ignoreCase)
            {
                foreach (var kv in values)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value ?? "";
                }
            }

            // 未找到匹配的占位符，保持原样
            return match.Value;
        });
    }

    /// <summary>
    /// 高效的模板字符串替换（使用 StringBuilder）
    /// 适用于大量替换场景
    /// </summary>
    /// <param name="template">模板字符串</param>
    /// <param name="values">占位符值字典（key 不包含大括号）</param>
    /// <param name="ignoreCase">是否忽略大小写，默认 false</param>
    /// <param name="ignoreUnderline">是否忽略下划线，默认 true</param>
    /// <returns>替换后的字符串</returns>
    public static string ReplacePlaceholdersFast(string template, Dictionary<string, string?> values, bool ignoreCase = false, bool ignoreUnderline = true)
    {
        if (string.IsNullOrEmpty(template) || values == null || values.Count == 0)
            return template;

        var sb = new StringBuilder(template.Length + 64);
        var i = 0;
        var len = template.Length;

        while (i < len)
        {
            // 查找下一个 {
            var start = template.IndexOf('{', i);
            if (start == -1)
            {
                // 没有更多占位符，追加剩余内容
                sb.Append(template, i, len - i);
                break;
            }

            // 追加 { 之前的内容
            if (start > i)
            {
                sb.Append(template, i, start - i);
            }

            // 查找对应的 }
            var end = template.IndexOf('}', start + 1);
            if (end == -1)
            {
                // 没有找到 }，追加剩余内容
                sb.Append(template, start, len - start);
                break;
            }

            // 提取占位符名称
            var key = template.Substring(start + 1, end - start - 1);
            // 查找替换值
            var found = false;

            if (values.TryGetValue(key, out string? value))
            {
                found = true;
            }
            else if (ignoreUnderline && key.Contains('_'))
            {
                key = key.Replace("_", "");
                if (values.TryGetValue(key, out value))
                {
                    found = true;
                }
            }
            else if (ignoreCase)
            {
                foreach (var kv in values)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = kv.Value;
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                sb.Append(value ?? "");
            }
            else
            {
                // 未找到匹配，保持原样
                sb.Append(template, start, end - start + 1);
            }

            i = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从 HttpContext 创建常用占位符字典
    /// </summary>
    public static Dictionary<string, string?> CreateContextPlaceholders(HttpContext context)
    {
        var clientIp = RequestUtil.GetClientIp(context.Request);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ClientIp"] = clientIp,
            ["ip"] = clientIp,
            ["Path"] = context.Request.Path.Value ?? "/",
            ["Method"] = context.Request.Method,
            ["Host"] = context.Request.Host.ToString(),
            ["Time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["Scheme"] = context.Request.Scheme,
            ["Port"] = context.Request.Host.Port?.ToString() ?? "",
            ["Query"] = context.Request.QueryString.ToString(),
            ["UserAgent"] = context.Request.Headers.UserAgent.ToString(),
            ["Referer"] = context.Request.Headers.Referer.ToString(),
        };
    }

    /// <summary>
    /// 从 HttpContext 创建常用占位符字典，并合并额外的值
    /// </summary>
    public static Dictionary<string, string?> CreateContextPlaceholders(HttpContext context, Dictionary<string, string?>? extraValues)
    {
        var result = CreateContextPlaceholders(context);
        if (extraValues != null)
        {
            foreach (var kv in extraValues)
            {
                result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// 格式化模板字符串，使用 HttpContext 中的信息
    /// </summary>
    public static string FormatTemplate(string template, HttpContext context, Dictionary<string, string?>? extraValues = null)
    {
        var values = CreateContextPlaceholders(context, extraValues);
        return ReplacePlaceholdersFast(template, values);
    }
}
