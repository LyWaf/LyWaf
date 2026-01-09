
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
}


public class ProtectService : IProtectService
{
    private ProtectOptions _options;
    private readonly IMemoryCache _cache;
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private List<Regex> argsRegexes;
    private List<Regex> postRegexes;

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
        List<Regex> regexes = [];
        regexes.AddRange(BuildRegexesFromFile(_options.CheckArgsFile));
        foreach (var reg in _options.RegexArgsList)
        {
            regexes.Add(new Regex(reg, RegexOptions.IgnoreCase));
        }
        argsRegexes = regexes;

        regexes = [];
        regexes.AddRange(BuildRegexesFromFile(_options.CheckPostFile));
        foreach (var reg in _options.RegexPostList)
        {
            regexes.Add(new Regex(reg, RegexOptions.IgnoreCase));
        }
        postRegexes = regexes;
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