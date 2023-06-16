using System.Collections.Concurrent;

namespace MSBuild.CompilerCache;

public class DictionaryBasedCache<TKey, TValue> : ICacheBase<TKey, TValue>
{
    private ConcurrentDictionary<TKey, TValue> _cache = new ConcurrentDictionary<TKey, TValue>();
    
    public bool Exists(TKey key) => _cache.ContainsKey(key);
    
    public TValue Get(TKey key)
    {
        _cache.TryGetValue(key, out var value);
        return value;
    }

    public bool Set(TKey key, TValue value) => _cache.TryAdd(key, value);
}