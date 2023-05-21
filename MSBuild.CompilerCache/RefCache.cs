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
            var json = File.ReadAllText(entryPath);
            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<RefDataWithOriginalExtract>(json);
            return data;
        }
        else
        {
            return null;
        }
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
            Cache.AtomicCopy(tmpFile.FullName, entryPath);
        }
    }
}