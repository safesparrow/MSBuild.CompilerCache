namespace MSBuild.CompilerCache;

public interface ICacheBase<TKey, TValue>
{
    bool Exists(TKey key);
    TValue Get(TKey key);
    bool Set(TKey key, TValue value);
}