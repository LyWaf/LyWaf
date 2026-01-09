
using System.IO.Compression;
using System.Net.Http;
using System.Threading.RateLimiting;
using LyWaf.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;


public interface IStatisticService
{
    public Task<string> GetMatchPath(string path);

    public bool IsWhitePath(string path);

    public StatisticOptions GetOption();
}


public class StatisticService : IStatisticService
{
    private StatisticOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StatisticService> _logger;

    private Dictionary<string, string> _isInPathStas = [];

    private Dictionary<string, int> _isHasNextPathStas = [];

    private const int HAS_ANY = 0x00000001;
    private const int HAS_MATCH = 0x00000002;
    private const int HAS_FULL = 0x00000004;

    private const int HAS_ALL = HAS_ANY | HAS_MATCH | HAS_FULL;

    public StatisticService(
        IOptionsMonitor<StatisticOptions> options, IServiceProvider _serviceProvider, IConfiguration configuration, IMemoryCache cache,
        ILogger<StatisticService> logger)
    {
        _options = options.CurrentValue;
        // 可以订阅变更，但需注意生命周期和内存泄漏
        options.OnChange(newConfig =>
        {
            _options = newConfig;
            BuildStatistic();
        });
        ServiceLocator.Initialize(_serviceProvider);
        BuildStatistic();
        _cache = cache;
        _logger = logger;
    }

    private static int ConvertStrToSta(string str)
    {
        if (str == "*")
        {
            return HAS_ANY;
        }
        else if (str.StartsWith('{') && str.EndsWith('}'))
        {
            return HAS_MATCH;
        }
        else
        {
            return HAS_FULL;
        }
    }

    private void BuildStatistic()
    {
        var newPathStatistic = new Dictionary<string, string>();
        var newNextPathStatistic = new Dictionary<string, int>{
            {"", HAS_MATCH | HAS_FULL}
        };

        foreach (var limit in _options.LimitCc)
        {
            if (limit.Path.Length > 0)
            {
                _options.PathStas.Add(limit.Path);
            }
        }

        
        foreach (var path in _options.WhitePaths)
        {
            if (path.Length > 0)
            {
                _options.PathStas.Add(path);
            }
        }

        foreach (var path in _options.PathStas)
        {
            var l = path.Trim('/').Split('/');
            var buildList = new List<string>();
            for (int i = 0; i < l.Length; i++)
            {
                var str = l[i];
                var val = ConvertStrToSta(str);
                if ((val & HAS_ANY) != 0)
                {
                    buildList.Add(StatisticOptions.ANY);
                }
                else if ((val & HAS_MATCH) != 0)
                {
                    buildList.Add(StatisticOptions.MATCH);
                }
                else
                {
                    buildList.Add(str);
                }
                if (i == l.Length - 1)
                {
                    newPathStatistic.Add(string.Join(StatisticOptions.CONNECT, buildList), path);
                }
                else
                {
                    var c = string.Join(StatisticOptions.CONNECT, buildList);
                    val = ConvertStrToSta(l[i + 1]);
                    if (newNextPathStatistic.TryGetValue(c, out var v))
                    {
                        newNextPathStatistic[c] = v | val;
                    }
                    else
                    {
                        newNextPathStatistic[c] = val;
                    }
                }
            }
        }
        _isInPathStas = newPathStatistic;
        _isHasNextPathStas = newNextPathStatistic;
    }

    // false未匹配, true已匹配, 如果已匹配且返回有值则直接return
    private bool CheckVaild(LinkedList<string> nowList, out string? path)
    {
        var c = string.Join(StatisticOptions.CONNECT, nowList);
        if (_isInPathStas.TryGetValue(c, out string? val))
        {
            path = val;
            return true;
        }
        path = null;
        return false;
    }

    public async Task<string> GetMatchPath(string path)
    {
        var cacheKey = string.Format("MathPath:{0}", path);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(120);

            var l = path.Trim('/').Split('/');
            var buildList = new List<LinkedList<string>>
            {
                new([])
            };

            for (int i = 0; i < l.Length; i++)
            {
                var str = l[i];
                var newValidList = new List<LinkedList<string>>();
                foreach (var nowList in buildList)
                {
                    if (!_isHasNextPathStas.TryGetValue(string.Join(StatisticOptions.CONNECT, nowList), out var index))
                    {
                        continue;
                    }
                    if ((index & HAS_ANY) != 0)
                    {
                        nowList.AddLast(StatisticOptions.ANY);
                        if (CheckVaild(nowList, out var ret))
                        {
                            return ret!;
                        }
                        throw new Exception("不可能进入!");
                    }
                    if ((index & HAS_FULL) != 0)
                    {
                        var operList = nowList;
                        // 如果不存在后续则不需要拷贝
                        if ((index & HAS_MATCH) != 0)
                        {
                            operList = new LinkedList<string>(nowList);
                        }
                        operList.AddLast(str);
                        if (i == l.Length - 1 && CheckVaild(operList, out var ret))
                        {
                            return ret!;
                        }
                        else
                        {
                            newValidList.Add(operList);
                        }

                    }

                    if ((index & HAS_MATCH) != 0)
                    {
                        nowList.AddLast(StatisticOptions.MATCH);
                        if (i == l.Length - 1 && CheckVaild(nowList, out var ret))
                        {
                            return ret!;
                        }
                        else
                        {
                            newValidList.Add(nowList);
                        }
                    }
                }
                if (newValidList.Count == 0)
                {
                    return path;
                }
                buildList = newValidList;
            }
            return path;
        }) ?? path;

    }

    public bool IsWhitePath(string path)
    {
        return _options.WhitePaths.Contains(path);
    }

    public StatisticOptions GetOption()
    {
        return _options;
    }
}