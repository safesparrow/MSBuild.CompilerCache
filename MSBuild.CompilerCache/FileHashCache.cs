using System.Text;

namespace MSBuild.CompilerCache;

/// <summary>
/// Cache for file contents hashes. Helps avoiding reading contents of input files (source files and .dlls),
/// if their FileHashCacheKey hasn't changed.
/// </summary>
public class FileHashCache : ICacheBase<FileHashCacheKey, string>
{
    private readonly string _cacheDir;

    public FileHashCache(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public string EntryPath(CacheKey key) => Path.Combine(_cacheDir, key.Key);

    public static CacheKey ExtractKey(FileHashCacheKey key) => new CacheKey(Utils.ObjectToHash(key, Utils.DefaultHasher));
    
    public bool Exists(FileHashCacheKey originalKey)
    {
        var key = ExtractKey(originalKey);
        var entryPath = EntryPath(key);
        return File.Exists(entryPath);
    }

    public string Get(FileHashCacheKey originalKey)
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

    internal static T IOActionWithRetries<T>(Func<T> action)
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

    public bool Set(FileHashCacheKey originalKey, string value)
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
        
        return CompilationResultsCache.AtomicCopy(tmpFile.FullName, entryPath, throwIfDestinationExists: false);
    }
}