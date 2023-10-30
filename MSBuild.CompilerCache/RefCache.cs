using System.Text.Json;

namespace MSBuild.CompilerCache;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using IRefCache = ICacheBase<CacheKey, RefDataWithOriginalExtract>;

public class RefCache : IRefCache
{
    private readonly string _cacheDir;

    public RefCache(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public string EntryPath(CacheKey key) => Path.Combine(_cacheDir, $"{key.Key}.json");
    
    public bool Exists(CacheKey key)
    {
        var entryPath = EntryPath(key);
        return File.Exists(entryPath);
    }

    public RefDataWithOriginalExtract? Get(CacheKey key)
    {
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            RefDataWithOriginalExtract Read()
            {
                using var fs = File.OpenRead(entryPath);
                return JsonSerializer.Deserialize(fs,
                    RefDataWithOriginalExtractJsonContext.Default.RefDataWithOriginalExtract)!;
            }
            return IOActionWithRetries(Read);
        }
        else
        {
            return null;
        }
    }

    private static T IOActionWithRetries<T>(Func<T> action)
    {
        var attempts = 5;
        var retryDelay = 50;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return action();
            }
            catch(IOException e) 
            {
                if (attempt < attempts)
                {
                    var delay = (int)(Math.Pow(2, attempt-1) * retryDelay);
                    Thread.Sleep(delay);
                }
                else
                {
                    throw;
                }
            }
        }

        throw new InvalidOperationException("Unexpected code location reached");
    }

    public bool Set(CacheKey key, RefDataWithOriginalExtract data)
    {
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            return false;
        }

        using var tmpFile = new TempFile();
        {
            using var fs = tmpFile.File.OpenWrite();
            JsonSerializer.Serialize(data, RefDataWithOriginalExtractJsonContext.Default.RefDataWithOriginalExtract);
        }
        return CompilationResultsCache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
    }
}

public class InMemoryRefCache : IRefCache
{
    private readonly ConcurrentDictionary<CacheKey, RefDataWithOriginalExtract> _cache =
        new ConcurrentDictionary<CacheKey, RefDataWithOriginalExtract>();

    public bool Exists(CacheKey key) => _cache.ContainsKey(key);

    public RefDataWithOriginalExtract? Get(CacheKey key)
    {
        _cache.TryGetValue(key, out var value);
        return value;
    }

    public bool Set(CacheKey key, RefDataWithOriginalExtract data) => _cache.TryAdd(key, data);
}