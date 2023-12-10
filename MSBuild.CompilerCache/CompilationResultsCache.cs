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
public record FileExtract
{
    public string Name { get; }
    public string? ContentHash { get; }
    public long Length { get; }

    public FileExtract(string Name, string? ContentHash, long Length)
    {
        this.Name = Name;
        this.ContentHash = ContentHash ?? throw new Exception($"Null ContentHash for {Name} {Length}");
        this.Length = Length;
    }
    
    public FileExtract2 ToFileExtract2() => new(ContentHash);
}

[Serializable]
public record FileExtract2(string? ContentHash);

[JsonSerializable(typeof(FileExtract[]))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class FileExtractsJsonContext : JsonSerializerContext;

public class CompilationResultsCacheMetrics
{
    
}

/// <summary>
/// All compilation inputs, used to generate a hash for caching.
/// </summary>
/// <param name="Files"></param>
/// <param name="Props"></param>
/// <param name="OutputFiles"></param>
[Serializable]
public record FullExtract(FileExtract[] Files, KeyValuePair<string,string>[] Props, string[] OutputFiles)
{
    public FullExtract(FileExtract[] Files, (string, string)[] Props, string[] OutputFiles)
    : this(Files, Props.Select(x => new KeyValuePair<string,string>(x.Item1, x.Item2)).OrderBy(kvp => kvp.Key).ToArray(), OutputFiles)
    {}
}

/// <summary>
/// An extended version of <see cref="FullExtract" /> 
/// </summary>
[Serializable]
public record LocalFileExtract
{
    public LocalFileExtract(FileHashCacheKey Info, string Hash)
    {
        this.Info = Info;
        this.Hash = Hash;
        if (Info.FullName == null) throw new Exception("File info name empty");
    }

    public FileHashCacheKey Info { get; set; }
    public string Path => Info.FullName;
    public long Length => Info.Length;
    public DateTime LastWriteTimeUtc => Info.LastWriteTimeUtc;
    public string Hash { get; set; }

    public FileExtract ToFileExtract() =>
        new(Name: System.IO.Path.GetFileName(Path), ContentHash: Hash, Length: Length);
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
        CacheFileName = GetCacheFileName();
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
public record LocalInputs(InputResult[] Files, KeyValuePair<string,string>[] Props, OutputItem[] OutputFiles)
{
    public LocalInputs(InputResult[] Files, (string, string)[] Props, OutputItem[] OutputFiles)
        : this(Files, Props.Select(x => new KeyValuePair<string,string>(x.Item1, x.Item2)).ToArray(), OutputFiles)
    {}

    public LocalInputsSlim ToSlim() =>
        new LocalInputsSlim(Files: Files.Select(f => f.fileHashCacheKey).ToArray(), Props, OutputFiles);
}

[Serializable]
public record LocalInputsSlim(LocalFileExtract[] Files, KeyValuePair<string, string>[] Props, OutputItem[] OutputFiles)
{
    public LocalInputsSlim(LocalFileExtract[] Files, (string, string)[] Props, OutputItem[] OutputFiles):
        this(Files, Props.Select(x => new KeyValuePair<string,string>(x.Item1, x.Item2)).ToArray(), OutputFiles)
    {}
    
    public FullExtract ToFullExtract()
    {
        return new FullExtract(Files: Files.Select(f => f.ToFileExtract()).ToArray(), Props: Props,
            OutputFiles: OutputFiles.Select(o => o.Name).ToArray());
    }
}

[Serializable]
public record AllCompilationMetadata(CompilationMetadata Metadata, LocalInputsSlim LocalInputs);

[JsonSerializable(typeof(AllCompilationMetadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class AllCompilationMetadataJsonContext : JsonSerializerContext;

[Serializable]
public record InputResult(FileStream f, LocalFileExtract fileHashCacheKey);

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
    /// <param name="moveInsteadOfCopy"></param>
    /// <returns></returns>
    public static bool AtomicCopyOrMove(FileInfo source, FileInfo destination, bool throwIfDestinationExists = true,
        bool moveInsteadOfCopy = false)
    {
        using var activity = Tracing.Source.StartActivity("AtomicCopyOrMove");
        activity?.SetTag("source", source.FullName);
        activity?.SetTag("destination", destination.FullName);
        activity?.SetTag("moveInsteadOfCopy", moveInsteadOfCopy);
        var dir = destination.DirectoryName!;
        var tmpDestination = Path.Combine(dir, $".__tmp_{Guid.NewGuid()}");
        FileInfo tmp;
        if (moveInsteadOfCopy)
        {
            source.MoveTo(tmpDestination);
            tmp = source;
        }
        else
        {
            tmp = source.CopyTo(tmpDestination);
        }

        try
        {
            tmp.MoveTo(destination.FullName, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            tmp.Delete();
            if (!throwIfDestinationExists && File.Exists(destination.FullName))
            {
                return false;
            }

            throw;
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
        Console.WriteLine($"Cache Set {key}. Dir {dir}");
        dir.Create();

        var outputFile = new FileInfo(Path.Combine(dir.FullName, resultZipToBeMoved.Name));

        if (!outputFile.Exists)
        {
            AtomicCopyOrMove(resultZipToBeMoved, outputFile, throwIfDestinationExists: false, moveInsteadOfCopy: true);
        }

        var extractFile = new FileInfo(ExtractPath(key));
        if (!extractFile.Exists)
        {
            var tmpFile = new FileInfo(TempFile.GetTempFilePath());
            {
                await using var fs = tmpFile.OpenWrite();
                await JsonSerializer.SerializeAsync(fs, fullExtract, FullExtractJsonContext.Default.FullExtract);
            }
            AtomicCopyOrMove(tmpFile, extractFile, throwIfDestinationExists: false, moveInsteadOfCopy: true);
        }
    }

    public Task<string?> GetAsync(CacheKey key)
    {
        var dir = new DirectoryInfo(CacheDir(key));
        Console.WriteLine($"Cache Get {key}. Dir {dir}");
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