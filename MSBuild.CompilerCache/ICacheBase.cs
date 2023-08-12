namespace MSBuild.CompilerCache;

public interface ICacheBase<TKey, TValue> where TValue : class
{
    bool Exists(TKey key);
    TValue? Get(TKey key);
    bool Set(TKey key, TValue value);

    sealed TValue GetOrSet(TKey key, Func<TKey, TValue> creator)
    {
        var value = Get(key);
        if (value == null)
        {
            value = creator(key);
            Set(key, value);
        }
        return value;
    }
}