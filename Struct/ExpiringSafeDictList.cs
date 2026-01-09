
namespace LyWaf.Struct;


public class ExpiringSafeDictList<TKey, TValue> : ExpiringSafeDictionary<TKey, LinkedList<TValue>> where TKey : notnull
{
    public int LPush(TKey key, TValue value)
    {
        int len = 1;
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                val!.Value.AddFirst(value);
                len = val!.Value.Count;
            }
            else
            {
                var expiringValue = new ExpiringValue<LinkedList<TValue>>
                {
                    Value = new([value]),
                    ExpiryTime = _defaultExpiration.HasValue ? DateTime.UtcNow.Add(_defaultExpiration.Value) : null,
                    LastAccessTime = DateTime.UtcNow
                };
                _dictionary[key] = expiringValue;
            }
            return len;
        }
    }

    public int RPush(TKey key, TValue value)
    {
        int len = 1;
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                val!.Value.AddLast(value);
                len = val!.Value.Count;
            }
            else
            {
                var expiringValue = new ExpiringValue<LinkedList<TValue>>
                {
                    Value = new([value]),
                    ExpiryTime = _defaultExpiration.HasValue ? DateTime.UtcNow.Add(_defaultExpiration.Value) : null,
                    LastAccessTime = DateTime.UtcNow
                };
                _dictionary[key] = expiringValue;
            }
            return len;
        }
    }

    public int LTrim(TKey key, int start, int len)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                LinkedList<TValue> newList = new([]);
                var endIdx = start + len;
                var nowIdx = 0;
                var current = val!.Value.First;
                while (current != null && nowIdx < endIdx)
                {
                    if (nowIdx >= start)
                    {
                        newList.AddLast(current.Value);
                    }
                    current = current.Next;
                }
                val.Value = newList;
                return newList.Count;
            }
            else
            {
                return 0;
            }
        }
    }


    public int RTrim(TKey key, int start, int len)
    {
        lock (_lockObject)
        {
            if (InnerTryGetValue(key, out var val))
            {
                LinkedList<TValue> newList = new([]);
                var endIdx = start + len;
                var nowIdx = 0;
                var current = val!.Value.Last;
                while (current != null && nowIdx < endIdx)
                {
                    if (nowIdx >= start)
                    {
                        newList.AddFirst(current.Value);
                    }
                    current = current.Previous;
                }
                val.Value = newList;
                return newList.Count;
            }
            else
            {
                return 0;
            }
        }
    }
}