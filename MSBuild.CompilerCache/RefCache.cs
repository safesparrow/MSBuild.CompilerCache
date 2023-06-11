using System.CodeDom;
using System.Text.Json;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

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
                return System.Text.Json.JsonSerializer.Deserialize<RefDataWithOriginalExtract>(fs)!;
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
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            return;
        }
        else
        {
            var jsonOptions = new JsonSerializerOptions()
            {
                WriteIndented = true
            };
            using var tmpFile = new TempFile();
            {
                using var fs = tmpFile.File.OpenWrite();
                System.Text.Json.JsonSerializer.Serialize(fs, data, jsonOptions);
            }
            Cache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
        }
    }
}