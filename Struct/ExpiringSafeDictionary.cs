using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace LyWaf.Struct;

/// <summary>
/// 支持过期的线程安全字典
/// </summary>
/// <typeparam name="TKey">键类型</typeparam>
/// <typeparam name="TValue">值类型</typeparam>
public class ExpiringSafeDictionary<TKey, TValue> : IDisposable where TKey : notnull
{
    protected readonly Dictionary<TKey, ExpiringValue<TValue>> _dictionary;
    protected readonly TimeSpan? _defaultExpiration;
    protected readonly Timer _cleanupTimer;
    protected readonly object _lockObject = new();

    /// <summary>
    /// 过期值包装类
    /// </summary>
    protected class ExpiringValue<T>
    {
        public required T Value { get; set; }
        public DateTime? ExpiryTime { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }
        public DateTime LastAccessTime { get; set; }

        public bool IsExpired(DateTime currentTime)
        {
            if (SlidingExpiration.HasValue)
            {
                // 滑动过期：检查最后访问时间
                return currentTime > LastAccessTime.Add(SlidingExpiration.Value);
            }
            else if (ExpiryTime.HasValue)
            {
                // 绝对过期：检查过期时间
                return currentTime > ExpiryTime;
            }
            else
            {
                // 从不过期
                return false;
            }
        }

        public void UpdateAccessTime()
        {
            LastAccessTime = DateTime.UtcNow;
        }
    }

    // 事件定义
    public event EventHandler<ItemExpiredEventArgs<TKey>>? ItemExpired;
    public event EventHandler<CleanupEventArgs>? CleanupCompleted;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="defaultExpiration">默认过期时间</param>
    /// <param name="cleanupInterval">清理间隔</param>
    public ExpiringSafeDictionary(TimeSpan? defaultExpiration, TimeSpan cleanupInterval)
    {
        _dictionary = [];
        _defaultExpiration = defaultExpiration;
        _cleanupTimer = new Timer(CleanupExpiredItems, null, cleanupInterval, cleanupInterval);
    }

    /// <summary>
    /// 构造函数（使用默认清理间隔：1分钟）
    /// </summary>
    /// <param name="defaultExpiration">默认过期时间</param>
    public ExpiringSafeDictionary(TimeSpan? defaultExpiration)
        : this(defaultExpiration, TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>
    /// 默认构造函数（默认过期时间：30分钟，清理间隔：1分钟）
    /// </summary>
    public ExpiringSafeDictionary()
        : this(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>
    /// 添加或更新值（使用默认过期时间）
    /// </summary>
    public bool AddOrUpdate(TKey key, TValue value)
    {
        return AddOrUpdate(key, value, _defaultExpiration);
    }

    /// <summary>
    /// 添加或更新值（指定过期时间）
    /// </summary>
    public bool AddOrUpdate(TKey key, TValue value, TimeSpan? expiration = null)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                val!.Value = value;
                if (expiration != null)
                {
                    val.ExpiryTime = DateTime.UtcNow.Add(expiration.Value);
                }
                val.UpdateAccessTime();
            }
            else
            {
                var expiringValue = new ExpiringValue<TValue>
                {
                    Value = value,
                    ExpiryTime = expiration.HasValue ? DateTime.UtcNow.Add(expiration.Value) : null,
                    LastAccessTime = DateTime.UtcNow
                };
                _dictionary[key] = expiringValue;
            }
        }
        return true;
    }

    /// <summary>
    /// 添加或更新值（使用滑动过期）
    /// </summary>
    public bool AddOrUpdateSliding(TKey key, TValue value, TimeSpan slidingExpiration)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                val!.Value = value;
                val.ExpiryTime = DateTime.UtcNow.Add(slidingExpiration);
                val.SlidingExpiration = slidingExpiration;
                val.UpdateAccessTime();
            }
            else
            {
                var expiringValue = new ExpiringValue<TValue>
                {
                    Value = value,
                    ExpiryTime = DateTime.UtcNow.Add(slidingExpiration), // 初始过期时间
                    SlidingExpiration = slidingExpiration,
                    LastAccessTime = DateTime.UtcNow
                };
                _dictionary[key] = expiringValue;
            }
        }
        return true;
    }

    /// <summary>
    /// 尝试获取值（如果过期会自动移除）
    /// </summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        if (InnerTryGetValue(key, out var val))
        {
            value = val!.Value;
            return true;
        }
        value = default!;
        return false;
    }

    protected bool InnerTryGetValue(TKey key, out ExpiringValue<TValue>? value)
    {
        value = null;
        lock (_lockObject)
        {
            if (_dictionary.TryGetValue(key, out var expiringValue))
            {
                var now = DateTime.UtcNow;
                if (expiringValue!.IsExpired(now))
                {
                    _dictionary.Remove(key);
                    OnItemExpired(key, now);
                    return false;
                }
                // 更新访问时间（如果是滑动过期）
                if (expiringValue.SlidingExpiration.HasValue)
                {
                    expiringValue.UpdateAccessTime();
                }

                value = expiringValue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取或添加值
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory, TimeSpan? expiration)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                return val!.Value;
            }
            val = CreateExpiringValue(valueFactory(key), expiration);
            _dictionary[key] = val;
            return val.Value;
        }
    }

    /// <summary>
    /// 获取或添加值（使用默认过期时间）
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        return GetOrAdd(key, valueFactory, _defaultExpiration);
    }

    public long Incr(TKey key, long value, long init = 0, TimeSpan? expiration = null)
    {
        lock (_lockObject)
        {
            if (expiration == null)
            {
                expiration = _defaultExpiration;
            }
            try
            {
                if (InnerTryGetValue(key, out var val))
                {
                    val!.Value = (TValue)(object)(Convert.ToInt64(val.Value) + value);
                }
                else
                {
                    val = CreateExpiringValue((TValue)(object)(init + value), expiration);
                    _dictionary[key] = val;
                }
                return Convert.ToInt64(val.Value);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public double IncrDouble(TKey key, double value, double init = 0, TimeSpan? expiration = null)
    {
        lock (_lockObject)
        {
            if (expiration == null)
            {
                expiration = _defaultExpiration;
            }
            try
            {
                if (InnerTryGetValue(key, out var val))
                {
                    val!.Value = (TValue)(object)(Convert.ToDouble(val.Value) + value);
                }
                else
                {
                    val = CreateExpiringValue((TValue)(object)(init + value), expiration);
                    _dictionary[key] = val;
                }
                return Convert.ToDouble(val.Value);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    private static ExpiringValue<TValue> CreateExpiringValue(TValue value, TimeSpan? expiration)
    {
        return new ExpiringValue<TValue>
        {
            Value = value,
            ExpiryTime = expiration.HasValue ? DateTime.UtcNow.Add(expiration.Value) : null,
            LastAccessTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 清理过期项目
    /// </summary>
    private void CleanupExpiredItems(object? _state)
    {
        lock (_lockObject)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = new List<TKey>();

            foreach (var pair in _dictionary)
            {
                if (pair.Value.IsExpired(now))
                {
                    expiredKeys.Add(pair.Key);
                }
            }

            // 移除过期项目并触发事件
            foreach (var key in expiredKeys)
            {
                if (_dictionary.Remove(key, out var _))
                {
                    OnItemExpired(key, now);
                }
            }

            // 触发清理事件
            CleanupCompleted?.Invoke(this, new CleanupEventArgs
            {
                RemovedCount = expiredKeys.Count,
                RemainingCount = _dictionary.Count,
                CleanupTime = now
            });
        }
    }

    /// <summary>
    /// 手动立即清理过期项目
    /// </summary>
    public void CleanupNow()
    {
        CleanupExpiredItems(null);
    }

    /// <summary>
    /// 移除指定键
    /// </summary>
    public bool Remove(TKey key)
    {
        lock (_lockObject)
        {
            return _dictionary.Remove(key, out _);
        }
    }

    /// <summary>
    /// 检查是否包含键（不检查过期）
    /// </summary>
    public bool ContainsKey(TKey key)
    {
        return _dictionary.ContainsKey(key);
    }

    /// <summary>
    /// 获取所有未过期的键
    /// </summary>
    public IEnumerable<TKey> GetValidKeys()
    {
        var now = DateTime.UtcNow;

        return _dictionary
            .Where(pair => !pair.Value.IsExpired(now))
            .Select(pair => pair.Key)
            .ToList();
    }

    /// <summary>
    /// 获取字典中项目的数量（包括过期的）
    /// </summary>
    public int Count => _dictionary.Count;

    /// <summary>
    /// 获取有效项目的数量（未过期的）
    /// </summary>
    public int ValidCount
    {
        get
        {
            var now = DateTime.UtcNow;
            return _dictionary.Count(pair => !pair.Value.IsExpired(now));
        }
    }

    /// <summary>
    /// 延长项目的过期时间
    /// </summary>
    public bool ExtendExpiration(TKey key, TimeSpan additionalTime)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var expiringValue))
            {
                var now = DateTime.UtcNow;
                if (!expiringValue!.IsExpired(now))
                {
                    if (expiringValue.ExpiryTime.HasValue)
                    {
                        expiringValue.ExpiryTime = expiringValue.ExpiryTime.Value.Add(additionalTime);
                    }
                    else
                    {
                        expiringValue.ExpiryTime = DateTime.UtcNow.Add(additionalTime);
                    }
                    expiringValue.LastAccessTime = now;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 设置过期时间
    /// </summary>
    public bool Expire(TKey key, TimeSpan expireTime)
    {

        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var expiringValue))
            {
                var now = DateTime.UtcNow;
                if (!expiringValue!.IsExpired(now))
                {
                    expiringValue.ExpiryTime = now.Add(expireTime);
                    expiringValue.SlidingExpiration = null;
                    expiringValue.LastAccessTime = now;
                    return true;
                }
            }
        }
        return false;
    }


    /// <summary>
    /// 设置过期时间
    /// </summary>
    public bool ExpireAt(TKey key, DateTime dateTime)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var expiringValue))
            {
                var now = DateTime.UtcNow;
                if (!expiringValue!.IsExpired(now))
                {
                    expiringValue.ExpiryTime = dateTime;
                    expiringValue.SlidingExpiration = null;
                    expiringValue.LastAccessTime = now;
                    return true;
                }
            }
        }

        return false;
    }

    public bool ExpireSliding(TKey key, TimeSpan slidingTime)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var expiringValue))
            {
                var now = DateTime.UtcNow;
                if (!expiringValue!.IsExpired(now))
                {
                    expiringValue.ExpiryTime = null;
                    expiringValue.SlidingExpiration = slidingTime;
                    expiringValue.LastAccessTime = now;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 删除过期时间
    /// </summary>
    public bool DelTtl(TKey key)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var expiringValue))
            {
                expiringValue!.ExpiryTime = null;
                expiringValue!.SlidingExpiration = null;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 获取项目的剩余生存时间
    /// </summary>
    public TimeSpan? GetTimeToLive(TKey key)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var expiringValue))
            {
                var now = DateTime.UtcNow;
                if (!expiringValue!.IsExpired(now))
                {
                    if (expiringValue.SlidingExpiration.HasValue)
                    {
                        return expiringValue.LastAccessTime.Add(expiringValue.SlidingExpiration.Value) - now;
                    }
                    else if (expiringValue.ExpiryTime.HasValue)
                    {
                        return expiringValue.ExpiryTime - now;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 获取所有有效项目的快照
    /// </summary>
    public Dictionary<TKey, TValue> GetSnapshot()
    {
        lock (_lockObject)
        {
            var now = DateTime.UtcNow;

            return _dictionary
                .Where(pair => !pair.Value.IsExpired(now))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value);
        }
    }

    /// <summary>
    /// 批量添加项目
    /// </summary>
    public void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> items, TimeSpan? expiration)
    {

        foreach (var item in items)
        {
            AddOrUpdate(item.Key, item.Value, expiration);
        }
    }

    /// <summary>
    /// 批量添加项目（使用默认过期时间）
    /// </summary>
    public void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> items)
    {
        AddRange(items, _defaultExpiration);
    }

    /// <summary>
    /// 清空字典
    /// </summary>
    public void Clear()
    {
        lock (_lockObject)
        {
            _dictionary.Clear();
        }
    }

    public object GetLockObject() {
        return _lockObject;
    }

    public void DoLockFunc(Func<bool> func)
    {
        lock (_lockObject)
        {
            var _ = func();
        }
    }

    public Dictionary<TKey, TValue> Dump()
    {
        lock (_lockObject)
        {
            Dictionary<TKey, TValue> ret = [];
            foreach (var v in _dictionary)
            {
                ret[v.Key] = v.Value.Value;
            }
            return ret;
        }
    }

    public Dictionary<TKey, TValue> DumpAndClear()
    {
        lock (_lockObject)
        {
            var ret = Dump();
            _dictionary.Clear();
            return ret;
        }
    }

    public Dictionary<TKey, TValue> FilterRemove(Func<(TKey, TValue), bool> filter)
    {
        lock (_lockObject)
        {
            Dictionary<TKey, TValue> ret = [];
            List<TKey> removeKey = [];
            foreach (var v in _dictionary)
            {
                if (filter((v.Key, v.Value.Value)))
                {
                    removeKey.Add(v.Key);
                    ret.Add(v.Key, v.Value.Value);
                }
            }
            foreach (var k in removeKey)
            {
                _dictionary.Remove(k);
            }
            return ret;
        }
    }

    public void DoLockKeyFunc(TKey key, Func<TKey, TValue> create, Func<TValue, bool> func)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                var _ = func(val!.Value);
            }
            else
            {
                var value = CreateExpiringValue(create(key), _defaultExpiration);
                _dictionary[key] = value;
                var _ = func(value.Value);
            }
        }
    }

    /// <summary>
    /// 项目过期事件处理
    /// </summary>
    protected virtual void OnItemExpired(TKey key, DateTime removeTime)
    {
        ItemExpired?.Invoke(this, new ItemExpiredEventArgs<TKey>
        {
            Key = key,
            RemoveTime = removeTime
        });
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _dictionary.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 析构函数
    /// </summary>
    ~ExpiringSafeDictionary()
    {
        Dispose();
    }
}

/// <summary>
/// 项目过期事件参数
/// </summary>
public class ItemExpiredEventArgs<TKey> : EventArgs where TKey : notnull
{
    public required TKey Key { get; set; }
    public DateTime RemoveTime { get; set; }
}

/// <summary>
/// 清理事件参数
/// </summary>
public class CleanupEventArgs : EventArgs
{
    public int RemovedCount { get; set; }
    public int RemainingCount { get; set; }
    public DateTime CleanupTime { get; set; }
}

// 使用示例和测试类
public class ExpiringDictionaryExample
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public static void Demo()
    {
        // 创建过期字典，默认30分钟过期，每1分钟清理一次
        using var expiringDict = new ExpiringSafeDictionary<string, object>(
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(1)
        );

        // 订阅事件
        expiringDict.ItemExpired += (sender, e) =>
        {
            _logger.Info("项目过期: {Key}, 移除时间: {RemoveTime}", e.Key, e.RemoveTime);
        };

        expiringDict.CleanupCompleted += (sender, e) =>
        {
            _logger.Info("清理完成: 移除了 {RemovedCount} 个项目，剩余 {RemainingCount} 个项目", e.RemovedCount, e.RemainingCount);
        };

        // 添加项目
        expiringDict.AddOrUpdate("user:1001", new { Name = "张三", Age = 25 });
        expiringDict.AddOrUpdate("config:timeout", 5000, TimeSpan.FromMinutes(10));
        expiringDict.AddOrUpdateSliding("session:token", "abc123", TimeSpan.FromMinutes(5));

        // 获取项目
        if (expiringDict.TryGetValue("user:1001", out object user))
        {
            _logger.Info("找到用户: {User}", user);
        }

        // 获取或添加
        var data = expiringDict.GetOrAdd("cached:data", key => ExpensiveOperation(key));

        // 获取剩余时间
        var ttl = expiringDict.GetTimeToLive("user:1001");
        if (ttl.HasValue)
        {
            _logger.Info("用户数据剩余生存时间: {Ttl}", ttl.Value);
        }

        // 延长过期时间
        expiringDict.ExtendExpiration("user:1001", TimeSpan.FromHours(1));

        // 手动清理
        expiringDict.CleanupNow();

        // 获取快照
        var snapshot = expiringDict.GetSnapshot();
        _logger.Info("有效项目数量: {Count}", snapshot.Count);
    }

    private static object ExpensiveOperation(string key)
    {
        // 模拟耗时操作
        Thread.Sleep(100);
        return new { Data = $"处理后的 {key}", Timestamp = DateTime.UtcNow };
    }
}
