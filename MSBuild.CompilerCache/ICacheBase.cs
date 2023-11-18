namespace MSBuild.CompilerCache;

public interface ICacheBase<TKey, TValue> where TValue : class
{
    bool Exists(TKey key);
    Task<TValue?> GetAsync(TKey key);
    Task<bool> SetAsync(TKey key, TValue value);
}

public class CacheIncStats
{
    public OperationStats Get { get; } = new();
    public OperationStats Set { get; } = new();
}

public class OperationStats
{
    public int Count { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }
    public TimeCounter Time { get; } = new TimeCounter();
}
