namespace MSBuild.CompilerCache;

public interface ICacheBase<TKey, TValue> where TValue : class
{
    bool Exists(TKey key);
    Task<TValue?> GetAsync(TKey key);
    bool Set(TKey key, TValue value) => SetAsync(key, value).GetAwaiter().GetResult();
    Task<bool> SetAsync(TKey key, TValue value);

    sealed async Task<TValue> GetOrSet(TKey key, Func<TKey, TValue> creator)
    {
        var value = await GetAsync(key);
        if (value == null)
        {
            value = creator(key);
            await SetAsync(key, value);
        }
        return value;
    }
}