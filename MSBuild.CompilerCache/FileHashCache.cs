using System.Text;

namespace MSBuild.CompilerCache;

public class FileHashCache : ICacheBase<FileCacheKey, string>
{
    private readonly string _cacheDir;

    public FileHashCache(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public string EntryPath(CacheKey key) => Path.Combine(_cacheDir, key.Key);

    public CacheKey ExtractKey(FileCacheKey key) => new CacheKey(Utils.ObjectToHash(key));
    
    public bool Exists(FileCacheKey originalKey)
    {
        var key = ExtractKey(originalKey);
        var entryPath = EntryPath(key);
        return File.Exists(entryPath);
    }

    public string Get(FileCacheKey originalKey)
    {
        var key = ExtractKey(originalKey);
        var entryPath = EntryPath(key);
        var fi = new FileInfo(entryPath);
        if (fi.Exists)
        {
            string Read()
            {
                using var fs = fi.OpenText();
                return fs.ReadToEnd();
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

    public bool Set(FileCacheKey originalKey, string value)
    {
        var key = ExtractKey(originalKey);
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            return false;
        }

        using var tmpFile = new TempFile();
        {
            using var fs = tmpFile.File.OpenWrite();
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            sw.Write(value);
        }
        
        return Cache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
    }
}