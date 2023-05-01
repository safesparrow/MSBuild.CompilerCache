using System.Collections.Immutable;
namespace MSBuild.CompilerCache;
using CompilationOutputs = ImmutableArray<RelativePath>;

public static class StringExtensions
{
    public static AbsolutePath AsAbsolutePath(this string x) => new AbsolutePath(x);
    public static RelativePath AsRelativePath(this string x) => new RelativePath(x);
}

public record AbsolutePath(string Path)
{
    public static implicit operator string(AbsolutePath path) => path.Path;
}

public record RelativePath(string Path)
{
    public AbsolutePath ToAbsolute(AbsolutePath relativeTo) =>
        System.IO.Path.Combine(relativeTo, Path).AsAbsolutePath();
    
    public static implicit operator string(RelativePath path) => path.Path;
}

/*
 * CacheKey - Hash of compilation inputs - property values and file contents (FullExtract)
 * FullExtract - All information used to generate a CacheKey. Does not include full paths, timestamps, hostnames.
 * LocalFullMetadata - All information about the local compilation, including local environment and all inputs.
 *
 * Information known before compilation:
 * - environment: username, host, start time
 * - local inputs: local files with their hash, and properties
 * - outputs
 *
 * Information known after compilation:
 * - same as above (with possibly new file info)
 * 
 */

[Serializable]
public record FileExtract(string Name, string Hash, long Length);

// TODO Use a dictionary to disambiguate files in different Item lists 
[Serializable]
public record FullExtract(FileExtract[] Files, string[] Props, string[] OutputFiles);

[Serializable]
public record LocalFileExtract(string Path, string Hash, long Length, DateTime LastWriteTimeUtc)
{
    public FileExtract ToFileExtract() => new(Name: System.IO.Path.GetFileName(Path), Hash: Hash, Length: Length);
}

/// <summary>
/// Information about the environment that is not part of the compilation inputs,
/// but could potentially cause difference in results,
/// and can be recorded for investigation.
/// </summary>
[Serializable]
public record PreCompilationMetadata(string Hostname, string Username, DateTime StartTimeUtc, string WorkingDirectory);

[Serializable]
public record PostCompilationMetadata(string Hostname, string Username, DateTime StartTimeUtc, DateTime StopTimeUtc);

[Serializable]
public record OutputItem(string Name, string LocalPath);

/// <summary>
/// Used to describe raw compilation inputs, with absolute paths and machine-specific values.
/// Used only for debugging purposes, stored alongside cache items.
/// </summary>
[Serializable]
public record LocalInputs(LocalFileExtract[] Files, string[] Props, OutputItem[] OutputFiles)
{
    public FullExtract ToFullExtract()
    {
        return new FullExtract(Files: Files.Select(f => f.ToFileExtract()).ToArray(), Props: Props, OutputFiles: OutputFiles.Select(o => o.Name).ToArray());
    }
}

[Serializable]
public record AllCompilationMetadata(PostCompilationMetadata Metadata, LocalInputs LocalInputs);


public record CacheKey(string Key)
{
    public static implicit operator string(CacheKey key) => key.Key;
}

public interface ICache
{
    bool Exists(CacheKey key);
    void Set(CacheKey key, AllCompilationMetadata metadata);
}

public class Cache : ICache
{
    private readonly string _baseCacheDir;

    public Cache(string baseCacheDir)
    {
        _baseCacheDir = baseCacheDir;
    }
    
    private string CacheDir(string key) => Path.Combine(_baseCacheDir, key);
    private string MarkerPath(string key) => Path.Combine(CacheDir(key), "extract.json");
    
    public bool Exists(CacheKey key)
    {
        var markerPath = MarkerPath(key);
        return File.Exists(markerPath);
    }

    public void Set(CacheKey key, AllCompilationMetadata metadata)
    {
        throw new NotImplementedException();
    }
}