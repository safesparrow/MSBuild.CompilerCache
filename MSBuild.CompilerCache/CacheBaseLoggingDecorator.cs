using System.Diagnostics;

namespace MSBuild.CompilerCache;

public class CacheBaseLoggingDecorator<TKey, TValue> : ICacheBase<TKey, TValue> where TValue : class
{
    private readonly ICacheBase<TKey,TValue> _cache;
    private readonly CacheIncStats _stats;
    
    // TODO synchronisation
    public CacheIncStats Stats => _stats;

    public CacheBaseLoggingDecorator(ICacheBase<TKey, TValue> cache)
    {
        _cache = cache;
        _stats = new CacheIncStats();
    }
    
    public bool Exists(TKey key)
    {
        return _cache.Exists(key);
    }

    public async Task<TValue?> GetAsync(TKey key)
    {
        var sw = Stopwatch.StartNew();
        var res = await _cache.GetAsync(key);
        sw.Stop();
        lock (_stats)
        {
            _stats.Get.Count++;
            if(res != null) _stats.Get.Hits++;
            else _stats.Get.Misses++;
            _stats.Get.Time.Add(sw.Elapsed);
        }

        return res;
    }

    public async Task<bool> SetAsync(TKey key, TValue value)
    {
        var sw = Stopwatch.StartNew();
        var didSet = await _cache.SetAsync(key, value);
        sw.Stop();
        lock (_stats)
        {
            _stats.Set.Count++;
            if(didSet) _stats.Set.Hits++;
            else _stats.Set.Misses++;
            _stats.Set.Time.Add(sw.Elapsed);
        }

        return didSet;
    }
}

public static class CacheBaseLoggingDecoratorExtensions
{
    public static CacheBaseLoggingDecorator<TKey, TValue> WithLogging<TKey, TValue>(this ICacheBase<TKey, TValue> cache)
        where TValue : class
    {
        return new CacheBaseLoggingDecorator<TKey, TValue>(cache);
    }
}
