using System.Collections.Concurrent;

namespace MSBuild.CompilerCache;

public class DictionaryBasedCache<TKey, TValue> : ICacheBase<TKey, TValue> where TValue : class where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _cache = new ConcurrentDictionary<TKey, TValue>();
    
    public bool Exists(TKey key) => _cache.ContainsKey(key);
    
    public Task<TValue?> GetAsync(TKey key)
    {
        _cache.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task<bool> SetAsync(TKey key, TValue value) => Task.FromResult(_cache.TryAdd(key, value));
}