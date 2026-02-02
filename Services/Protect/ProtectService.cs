
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using LyWaf.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using NLog;
using Xunit;

namespace LyWaf.Services.Protect;
public interface IProtectService
{
    public ProtectOptions GetOptions();

    public Task<string?> CheckArgsAttck(HttpContext context);
    public Task<string?> CheckPostAttck(HttpContext context);

    /// <summary>
    /// 获取 Args 检测正则列表
    /// </summary>
    List<string> GetArgsRegexList();

    /// <summary>
    /// 获取 Post 检测正则列表
    /// </summary>
    List<string> GetPostRegexList();

    /// <summary>
    /// 添加 Args 检测正则
    /// </summary>
    bool AddArgsRegex(string pattern);

    /// <summary>
    /// 移除 Args 检测正则
    /// </summary>
    bool RemoveArgsRegex(string pattern);

    /// <summary>
    /// 添加 Post 检测正则
    /// </summary>
    bool AddPostRegex(string pattern);

    /// <summary>
    /// 移除 Post 检测正则
    /// </summary>
    bool RemovePostRegex(string pattern);
}


public class ProtectService : IProtectService
{
    private ProtectOptions _options;
    private readonly IMemoryCache _cache;
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private List<Regex> argsRegexes;
    private List<Regex> postRegexes;

    // 动态添加的正则规则
    private readonly HashSet<string> _dynamicArgsRegex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dynamicPostRegex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _regexLock = new();

    public ProtectService(
        IOptionsMonitor<ProtectOptions> options, IMemoryCache cache)
    {
        argsRegexes = [];
        postRegexes = [];
        _options = options.CurrentValue;
        _cache = cache;
        // 可以订阅变更，但需注意生命周期和内存泄漏
        options.OnChange(newConfig =>
        {
            _options = newConfig;
            BuildRegexes();
        });
        BuildRegexes();
    }

    public ProtectOptions GetOptions()
    {
        return _options;
    }

    private void BuildRegexes()
    {
        lock (_regexLock)
        {
            List<Regex> regexes = [];
            regexes.AddRange(BuildRegexesFromFile(_options.CheckArgsFile));
            foreach (var reg in _options.RegexArgsList)
            {
                regexes.Add(new Regex(reg, RegexOptions.IgnoreCase));
            }
            // 添加动态规则
            foreach (var reg in _dynamicArgsRegex)
            {
                try
                {
                    regexes.Add(new Regex(reg, RegexOptions.IgnoreCase));
                }
                catch (Exception ex)
                {
                    _logger.Warn("无效的动态 Args 正则: {Pattern}, 错误: {Error}", reg, ex.Message);
                }
            }
            argsRegexes = regexes;

            regexes = [];
            regexes.AddRange(BuildRegexesFromFile(_options.CheckPostFile));
            foreach (var reg in _options.RegexPostList)
            {
                regexes.Add(new Regex(reg, RegexOptions.IgnoreCase));
            }
            // 添加动态规则
            foreach (var reg in _dynamicPostRegex)
            {
                try
                {
                    regexes.Add(new Regex(reg, RegexOptions.IgnoreCase));
                }
                catch (Exception ex)
                {
                    _logger.Warn("无效的动态 Post 正则: {Pattern}, 错误: {Error}", reg, ex.Message);
                }
            }
            postRegexes = regexes;
        }
    }

    private List<Regex> BuildRegexesFromFile(string path)
    {
        var regexes = new List<Regex>();
        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                var reg = new Regex(line, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                regexes.Add(reg);
            }
        }
        catch (Exception e)
        {
            _logger.Info("读取正则{}时出错:{}", path, e);
        }
        return regexes;
    }

    public Task<string?> CheckArgsRegexMatch(string key)
    {
        return _cache.GetOrCreateAsync($"args_regex_{key}", async entry =>
        {
            var unescapse = SqlUtil.Unescape(key);
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            foreach (var reg in argsRegexes)
            {
                if (reg.IsMatch(unescapse))
                {
                    return $"检测到参数存在攻击: {key}";
                }
            }
            return null;
        });
    }


    public Task<string?> CheckPostRegexMatch(string key)
    {
        return _cache.GetOrCreateAsync($"post_regex_{key}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            foreach (var reg in postRegexes)
            {
                if (reg.IsMatch(key))
                {
                    return $"检测到参数存在攻击: {key}";
                }
            }
            return null;
        });
    }

    public async Task<string?> CheckArgsAttck(HttpContext context)
    {
        if (!_options.OpenArgsCheck)
        {
            return null;
        }
        foreach (var q in context.Request.Query)
        {
            foreach (var v in q.Value)
            {
                var reason = await CheckArgsRegexMatch(v ?? "");
                if (reason != null)
                {
                    return reason;
                }
            }
        }
        return null;
    }

    public async Task<string?> CheckPostAttck(HttpContext context)
    {
        if (!_options.OpenPostCheck)
        {
            return null;
        }
        context.Request.EnableBuffering();
        StreamReader sr = new(context.Request.Body);
        var content = await sr.ReadToEndAsync();
        context.Request.Body.Position = 0;
        var contentType = context.Request.ContentType ?? "";
        if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var obj = JObject.Parse(content);
                foreach (JProperty targetProp in obj.Properties())
                {
                    var v = targetProp.Value.ToString();
                    var reason = await CheckPostRegexMatch(v);
                    if (reason != null)
                    {
                        return reason;
                    }
                }
            }
            catch (Exception)
            {
                // await context.Response.WriteAsync("不合法的JSON结构");
                return null;
            }
        }
        else if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = QueryHelpers.ParseQuery(content);
            foreach (var q in parsed)
            {
                foreach (var v in q.Value)
                {
                    var reason = await CheckPostRegexMatch(v ?? "");
                    if (reason != null)
                    {
                        return reason;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 获取 Args 检测正则列表
    /// </summary>
    public List<string> GetArgsRegexList()
    {
        lock (_regexLock)
        {
            var list = new List<string>(_options.RegexArgsList);
            list.AddRange(_dynamicArgsRegex);
            return list;
        }
    }

    /// <summary>
    /// 获取 Post 检测正则列表
    /// </summary>
    public List<string> GetPostRegexList()
    {
        lock (_regexLock)
        {
            var list = new List<string>(_options.RegexPostList);
            list.AddRange(_dynamicPostRegex);
            return list;
        }
    }

    /// <summary>
    /// 添加 Args 检测正则
    /// </summary>
    public bool AddArgsRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        // 验证正则表达式是否有效
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }

        lock (_regexLock)
        {
            if (_dynamicArgsRegex.Add(pattern))
            {
                BuildRegexes();
                _logger.Info("已添加动态 Args WAF 规则: {Pattern}", pattern);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 移除 Args 检测正则
    /// </summary>
    public bool RemoveArgsRegex(string pattern)
    {
        lock (_regexLock)
        {
            if (_dynamicArgsRegex.Remove(pattern))
            {
                BuildRegexes();
                _logger.Info("已移除动态 Args WAF 规则: {Pattern}", pattern);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 添加 Post 检测正则
    /// </summary>
    public bool AddPostRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        // 验证正则表达式是否有效
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }

        lock (_regexLock)
        {
            if (_dynamicPostRegex.Add(pattern))
            {
                BuildRegexes();
                _logger.Info("已添加动态 Post WAF 规则: {Pattern}", pattern);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 移除 Post 检测正则
    /// </summary>
    public bool RemovePostRegex(string pattern)
    {
        lock (_regexLock)
        {
            if (_dynamicPostRegex.Remove(pattern))
            {
                BuildRegexes();
                _logger.Info("已移除动态 Post WAF 规则: {Pattern}", pattern);
                return true;
            }
        }
        return false;
    }
}

public class CalculatorTests
{
    private static void CheckMatch(string reg, string val)
    {
        val = SqlUtil.Unescape(val);
        Assert.Matches(reg, val);
    }

    [Fact]
    public void TestAddition()
    {
        var reg = @"(?:(union(.|\n)*select))";
        CheckMatch(reg, "union all select");
        CheckMatch(reg, "union\nselect");
        CheckMatch(reg, "uni/**/on\nselect");
    }
}