using System.CodeDom;
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
            var json = ReadFileWithRetries(entryPath);
            var data = JsonConvert.DeserializeObject<RefDataWithOriginalExtract>(json);
            return data;
        }
        else
        {
            return null;
        }
    }

    private static string ReadFileWithRetries(string entryPath)
    {
        var attempts = 5;
        var retryDelay = 50;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return File.ReadAllText(entryPath);
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
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(data, Formatting.Indented);
            using var tmpFile = new TempFile();
            File.WriteAllText(tmpFile.FullName, json);
            Cache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
        }
    }
}