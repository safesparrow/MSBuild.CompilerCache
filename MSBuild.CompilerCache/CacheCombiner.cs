using IRefCache = MSBuild.CompilerCache.ICacheBase<MSBuild.CompilerCache.CacheKey, MSBuild.CompilerCache.RefDataWithOriginalExtract>;

namespace MSBuild.CompilerCache;

public class CacheCombiner<TKey, TValue> : ICacheBase<TKey, TValue> where TValue : class
{
    private readonly ICacheBase<TKey, TValue> _cache1;
    private readonly ICacheBase<TKey, TValue> _cache2;

    public CacheCombiner(ICacheBase<TKey, TValue> cache1, ICacheBase<TKey, TValue> cache2)
    {
        _cache1 = cache1;
        _cache2 = cache2;
    }

    public bool Exists(TKey key) => _cache1.Exists(key) || _cache2.Exists(key);

    public TValue? Get(TKey key)
    {
        var cache1Res = _cache1.Get(key);
        if (cache1Res != null)
        {
            return cache1Res;
        }
        else
        {
            var cache2Res = _cache2.Get(key);
            if (cache2Res != null)
            {
                _cache1.Set(key, cache2Res);
                return cache2Res;
            }
            else
            {
                return null;
            }
        }
    }

    public bool Set(TKey key, TValue value)
    {
        if (_cache1.Set(key, value))
        {
            _cache2.Set(key, value);
            return true;
        }
        else
        {
            return false;
        }
    }
}

public static class CacheCombiner
{
    public static ICacheBase<TKey,TValue> Combine<TKey,TValue>(ICacheBase<TKey, TValue> first, ICacheBase<TKey,TValue> second) where TValue : class
    {
        return new CacheCombiner<TKey, TValue>(first, second);
    }    
}
