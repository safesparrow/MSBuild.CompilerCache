namespace MSBuild.CompilerCache;

public class CacheCombiner<TKey, TValue> : ICacheBase<TKey, TValue>
{
    private readonly ICacheBase<TKey, TValue> _cache1;
    private readonly ICacheBase<TKey, TValue> _cache2;

    public CacheCombiner(ICacheBase<TKey, TValue> cache1, ICacheBase<TKey, TValue> cache2)
    {
        _cache1 = cache1;
        _cache2 = cache2;
    }

    public bool Exists(TKey key) => _cache1.Exists(key) || _cache2.Exists(key);

    public TValue Get(TKey key) => _cache1.Get(key) ?? _cache2.Get(key);

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