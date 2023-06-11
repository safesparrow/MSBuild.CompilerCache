using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;

namespace MSBuild.CompilerCache;

public class RefCache : IRefCache
{
    private readonly string _cacheDir;
    private readonly InMemoryRefCache _inMemoryRefCache;

    public RefCache(string cacheDir, InMemoryRefCache? inMemoryRefCache = null)
    {
        _cacheDir = cacheDir;
        _inMemoryRefCache = inMemoryRefCache ?? new InMemoryRefCache();
        Directory.CreateDirectory(_cacheDir);
    }

    public string EntryPath(CacheKey key) => Path.Combine(_cacheDir, $"{key.Key}.json");
    
    public bool Exists(CacheKey key)
    {
        if (_inMemoryRefCache.Exists(key)) return true;
        var entryPath = EntryPath(key);
        return File.Exists(entryPath);
    }

    public RefDataWithOriginalExtract? Get(CacheKey key)
    {
        if (_inMemoryRefCache.Exists(key))
        {
            return _inMemoryRefCache.Get(key);
        }
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            RefDataWithOriginalExtract Read()
            {
                var lines = File.ReadAllLines(entryPath);
                return new RefDataWithOriginalExtract(
                    Ref: new RefData(
                        PublicRefHash: lines[0],
                        PublicAndInternalRefHash: lines[1],
                        InternalsVisibleTo: lines[2].Split('\t').ToImmutableArray()
                    ),
                    Original: new LocalFileExtract(
                        Path: lines[3],
                        Length: long.Parse(lines[4]),
                        LastWriteTimeUtc: string.IsNullOrEmpty(lines[5])
                            ? null
                            : DateTime.ParseExact(lines[5], "o", System.Globalization.CultureInfo.InvariantCulture),
                        Hash: string.IsNullOrEmpty(lines[6]) ? null : lines[5]
                    )
                );
                //return System.Text.Json.JsonSerializer.Deserialize<RefDataWithOriginalExtract>(fs)!;
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

    public void Set(CacheKey key, RefDataWithOriginalExtract data)
    {
        _inMemoryRefCache.Set(key, data);
        
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            return;
        }

        using var tmpFile = new TempFile();
        {
            using var fs = tmpFile.File.OpenWrite();
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            sw.WriteLine(data.Ref.PublicRefHash);
            sw.WriteLine(data.Ref.PublicAndInternalRefHash);
            sw.WriteLine(string.Join('\t', data.Ref.InternalsVisibleTo));
            sw.WriteLine(data.Original.Path);
            sw.WriteLine(data.Original.Length);
            sw.WriteLine(data.Original.LastWriteTimeUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "");
            sw.WriteLine(data.Original.Hash ?? "");
        }
        Cache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
    }
}

public class InMemoryRefCache : IRefCache
{
    private ConcurrentDictionary<CacheKey, RefDataWithOriginalExtract> _cache =
        new ConcurrentDictionary<CacheKey, RefDataWithOriginalExtract>();

    public bool Exists(CacheKey key) => _cache.ContainsKey(key);

    public RefDataWithOriginalExtract? Get(CacheKey key)
    {
        _cache.TryGetValue(key, out var value);
        return value;
    }

    public void Set(CacheKey key, RefDataWithOriginalExtract data)
    {
        _cache.TryAdd(key, data);
    }
}