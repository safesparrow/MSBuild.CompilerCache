using System.Text.Json;

namespace MSBuild.CompilerCache;

using System.Collections.Concurrent;
using IRefCache = ICacheBase<CacheKey, RefDataWithOriginalExtract>;

/// <summary>
/// File-based implementation of <see cref="IRefCache"/> for storing information about trimmed dlls and their hashes.
/// Stores all entries in a single directory, one .json file per entry.
/// </summary>
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

    public async Task<RefDataWithOriginalExtract?> GetAsync(CacheKey key)
    {
        var entryPath = EntryPath(key);
        if (File.Exists(entryPath))
        {
            async Task<RefDataWithOriginalExtract?> Read()
            {
                await using var fs = File.OpenRead(entryPath);
                return await JsonSerializer.DeserializeAsync(fs,
                    RefDataWithOriginalExtractJsonContext.Default.RefDataWithOriginalExtract);
            }
            return await FileHashCache.IOActionWithRetriesAsync(Read);
        }
        else
        {
            return null;
        }
    }

    public async Task<bool> SetAsync(CacheKey key, RefDataWithOriginalExtract data)
    {
        var entryFile = new FileInfo(EntryPath(key));
        if (entryFile.Exists)
        {
            return false;
        }

        using var tmpFile = new TempFile();
        {
            await using var fs = tmpFile.File.OpenWrite();
            await JsonSerializer.SerializeAsync(fs, data, RefDataWithOriginalExtractJsonContext.Default.RefDataWithOriginalExtract);
        }
        return CompilationResultsCache.AtomicCopyOrMove(tmpFile.File, entryFile, throwIfDestinationExists: false);
    }
}

public class InMemoryRefCache : DictionaryBasedCache<CacheKey, RefDataWithOriginalExtract>;