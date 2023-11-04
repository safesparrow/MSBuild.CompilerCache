using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MSBuild.CompilerCache;

/// <summary>
/// Properties of a compilation input file, used in <see cref="FullExtract"/>
/// </summary>
/// <param name="Name">Name of the file, without directory tree</param>
/// <param name="ContentHash">Strong hash of the contents of the file</param>
/// <param name="Length"></param>
[Serializable]
public record struct FileExtract(string Name, string? ContentHash, long Length);

// TODO Use a dictionary to disambiguate files in different Item lists 
/// <summary>
/// All compilation inputs, used to generate a hash for caching.
/// </summary>
/// <param name="Files"></param>
/// <param name="Props"></param>
/// <param name="OutputFiles"></param>
[Serializable]
public record FullExtract(FileExtract[] Files, (string, string)[] Props, string[] OutputFiles);

/// <summary>
/// An extended version of <see cref="FullExtract" /> 
/// </summary>
[Serializable]
public record LocalFileExtract
{
    public LocalFileExtract(FileHashCacheKey Info, string? Hash)
    {
        this.Info = Info;
        this.Hash = Hash;
        if(Info.FullName == null) throw new Exception("File info name empty");
    }

    public FileHashCacheKey Info { get; set; }
    public string Path => Info.FullName;
    public long Length => Info.Length;
    public DateTime LastWriteTimeUtc => Info.LastWriteTimeUtc;
    public string? Hash { get; set; }
    public FileExtract ToFileExtract() => new(Name: System.IO.Path.GetFileName(Path), ContentHash: Hash, Length: Length);

    public void Deconstruct(out FileHashCacheKey Info, out string? Hash)
    {
        Info = this.Info;
        Hash = this.Hash;
    }
}

/// <summary>
/// Information about the environment that is not part of the compilation inputs,
/// but could potentially cause difference in results,
/// and can be recorded for investigation.
/// </summary>
[Serializable]
public record CompilationMetadata(string Hostname, string Username, DateTime StopTimeUtc, string WorkingDirectory);

[Serializable]
public record OutputItem
{
    public static Regex NameRegex = new Regex("^[\\d\\w_\\-]+$", RegexOptions.Compiled);

    public OutputItem(string Name, string LocalPath)
    {
        if (!NameRegex.IsMatch(Name))
        {
            throw new ArgumentException(
                $"OutputItem Name must be an alphanumeric string to represent a filename, but given: '{Name}'.");
        }

        this.Name = Name;
        this.LocalPath = LocalPath;
        this.CacheFileName = GetCacheFileName();
    }

    public string CacheFileName { get; }

    public string GetCacheFileName()
    {
        if (Path.HasExtension(LocalPath))
        {
            return $"{Name}{Path.GetExtension(LocalPath)}";
        }

        return Name;
    }

    public string Name { get; init; }
    public string LocalPath { get; init; }
}

/// <summary>
/// Used to describe raw compilation inputs, with absolute paths and machine-specific values.
/// Used only for debugging purposes, stored alongside cache items.
/// </summary>
[Serializable]
public record LocalInputs(InputResult[] Files, (string, string)[] Props, OutputItem[] OutputFiles)
{
    public FullExtract ToFullExtract()
    {
        return new FullExtract(Files: Files.Select(f => f.fileHashCacheKey.ToFileExtract()).ToArray(), Props: Props,
            OutputFiles: OutputFiles.Select(o => o.Name).ToArray());
    }
}

[Serializable]
public record AllCompilationMetadata(CompilationMetadata Metadata, LocalInputs LocalInputs);

public record struct CacheKey(string Key)
{
    public static implicit operator string(CacheKey key) => key.Key;
}

/// <summary>
/// Cache for the main compilation results
/// </summary>
public interface ICompilationResultsCache
{
    bool Exists(CacheKey key);
    public sealed void Set(CacheKey key, FullExtract fullExtract, FileInfo resultZipToBeMoved) =>
        SetAsync(key, fullExtract, resultZipToBeMoved).GetAwaiter().GetResult();
    Task SetAsync(CacheKey key, FullExtract fullExtract, FileInfo resultZipToBeMoved);

    sealed string? Get(CacheKey key) => GetAsync(key).GetAwaiter().GetResult();
    Task<string?> GetAsync(CacheKey key);
}

public class CompilationResultsCache : ICompilationResultsCache
{
    private readonly string _baseCacheDir;

    public CompilationResultsCache(string baseCacheDir)
    {
        _baseCacheDir = baseCacheDir;
    }

    private string CacheDir(CacheKey key) => Path.Combine(_baseCacheDir, key);
    private string ExtractPath(CacheKey key) => Path.Combine(CacheDir(key), "extract.json");

    public bool Exists(CacheKey key)
    {
        var markerPath = ExtractPath(key);
        return File.Exists(markerPath);
    }

    /// <summary>
    /// Copy source to destination atomically, by first creating a temporary copy in the target directory,
    /// then renaming the copy to the target name.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    /// <param name="throwIfDestinationExists">If true, will throw if the destination already exists</param>
    /// <returns></returns>
    public static bool AtomicCopy(string source, string destination, bool throwIfDestinationExists = true, bool moveInsteadOfCopy = false)
    {
        var dir = Path.GetDirectoryName(destination)!;
        var tmpDestination = Path.Combine(dir, $".__tmp_{Guid.NewGuid()}");
        if (moveInsteadOfCopy)
        {
            File.Move(source, tmpDestination);
        }
        else
        {
            File.Copy(source, tmpDestination);
        }
        try
        {
            File.Move(tmpDestination, destination, overwrite: false);
            return true;
        }
        catch (IOException e)
        {
            if (!throwIfDestinationExists && File.Exists(destination))
            {
                return false;
            }
            else
            {
                throw;
            }
        }
        finally
        {
            File.Delete(tmpDestination);
        }
    }

    public CacheKey[] GetAllExistingKeys()
    {
        var options = new EnumerationOptions
            { ReturnSpecialDirectories = false, IgnoreInaccessible = true, RecurseSubdirectories = false };
        var fullNames = Directory.EnumerateDirectories(_baseCacheDir, "*", options);
        return fullNames
            .Select(Path.GetFileName)
            .Where(name => !name!.StartsWith('.'))
            .Select(name => new CacheKey(name!))
            .ToArray();
    }

    public async Task SetAsync(CacheKey key, FullExtract fullExtract, FileInfo resultZipToBeMoved)
    {
        var dir = new DirectoryInfo(CacheDir(key));
        dir.Create();
        var extractPath = ExtractPath(key);
        var outputPath = Path.Combine(dir.FullName, resultZipToBeMoved.Name);
        
        if (!File.Exists(outputPath))
        {
            AtomicCopy(resultZipToBeMoved.FullName, outputPath, throwIfDestinationExists: false, moveInsteadOfCopy: true);
        }
        
        if (!File.Exists(extractPath))
        {
            // TODO Serialise directly to the cache dir (as a tmp file)
            using var tmpFile = new TempFile();
            {
                await using var fs = tmpFile.File.OpenWrite();
                await JsonSerializer.SerializeAsync(fs, fullExtract, FullExtractJsonContext.Default.FullExtract);
            }
            AtomicCopy(tmpFile.FullName, extractPath, throwIfDestinationExists: false, moveInsteadOfCopy: true);
        }
    }

    public Task<string?> GetAsync(CacheKey key)
    {
        var dir = new DirectoryInfo(CacheDir(key));
        if (dir.Exists)
        {
            var extractPath = ExtractPath(key);
            if (File.Exists(extractPath))
            {
                var outputVersionsZips = GetOutputVersions(key);
                switch (outputVersionsZips.Length)
                {
                    case 0:
                        throw new Exception($"[Cache key={key}] Extract file exists, but no output files found.");
                    case > 1:
                        throw new Exception(
                            $"[Cache key={key}] Found {outputVersionsZips.Length} different outputs. Unable to pick one.");
                    default:
                    {
                        var tmpPath = Path.GetTempFileName();
                        File.Copy(outputVersionsZips[0], tmpPath, overwrite: true);
                        return Task.FromResult(tmpPath)!;
                    }
                }
            }
        }

        return Task.FromResult<string?>(null);
    }

    public int OutputVersionsCount(CacheKey key) => GetOutputVersions(key).Length;

    private string[] GetOutputVersions(CacheKey key)
    {
        return Directory.EnumerateFiles(CacheDir(key), "*.zip", SearchOption.TopDirectoryOnly).ToArray();
    }
}

[JsonSerializable(typeof(FullExtract))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class FullExtractJsonContext : JsonSerializerContext;
