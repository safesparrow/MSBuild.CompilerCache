using System.Text;
using System.Text.Json;

namespace MSBuild.CompilerCache;

/// <summary>
/// Cache for file contents hashes. Helps avoiding reading contents of input files (source files and .dlls),
/// if their FileHashCacheKey hasn't changed.
/// </summary>
public class FileHashCache : ICacheBase<FileHashCacheKey, string>
{
    private readonly string _cacheDir;
    private readonly IHash _hasher;
    private readonly LocatorAndPopulator.Counters _counters;

    public FileHashCache(string cacheDir, IHash hasher, LocatorAndPopulator.Counters counters)
    {
        _hasher = hasher;
        _counters = counters;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public string EntryPath(CacheKey key) => Path.Combine(_cacheDir, key.Key);

    public CacheKey ExtractKey(FileHashCacheKey key)
    {
        var bytes = JsonSerializerExt.SerializeToUtf8Bytes(key, _counters.JsonSerialise, FileHashCacheKeyJsonContext.Default.FileHashCacheKey);
        string hash = Utils.BytesToHash(bytes, _hasher);
        return new CacheKey(hash);
    }

    public bool Exists(FileHashCacheKey originalKey)
    {
        var key = ExtractKey(originalKey);
        var entryPath = EntryPath(key);
        return File.Exists(entryPath);
    }

    public async Task<string?> GetAsync(FileHashCacheKey originalKey)
    {
        using var activity = Tracing.Source.StartActivity("FileHashCache.GetAsync");
        var key = ExtractKey(originalKey);
        var entryPath = EntryPath(key);
        var fi = new FileInfo(entryPath);
        if (fi.Exists)
        {
            activity?.SetTag("cache.hit", true);
            Task<string> Read() => File.ReadAllTextAsync(entryPath);
            return await IOActionWithRetriesAsync(Read);
        }

        activity?.SetTag("cache.hit", false);
        return null;
    }
    
    internal static async Task<T> IOActionWithRetriesAsync<T>(Func<Task<T>> action)
    {
        var attempts = 5;
        var retryDelayBaseMs = 50;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch(IOException)
            {
                if (attempt < attempts)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt-1) * retryDelayBaseMs);
                    await Task.Delay(delay);
                }
                else
                {
                    throw;
                }
            }
        }

        throw new InvalidOperationException("Unexpected code location reached");
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

    public async Task<bool> SetAsync(FileHashCacheKey originalKey, string value)
    {
        using var activity = Tracing.Source.StartActivity("FileHashCache.SetAsync");
        var key = ExtractKey(originalKey);
        activity?.SetTag("key", key.Key);
        var entryFile = new FileInfo(EntryPath(key));
        bool exists = entryFile.Exists;
        activity?.SetTag("cache.exists", exists);
        if (exists)
        {
            return false;
        }

        using var tmpFile = new TempFile();
        {
            using var activity2 = Tracing.Source.StartActivity("WriteTempFile");
            await using var fs = tmpFile.File.OpenWrite();
            await using var sw = new StreamWriter(fs, Encoding.UTF8);
            await sw.WriteAsync(value);
        }
        
        return CompilationResultsCache.AtomicCopyOrMove(tmpFile.File, entryFile, throwIfDestinationExists: false);
    }
}
