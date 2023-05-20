using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using JetBrains.Refasmer;
using JetBrains.Refasmer.Filters;
using Newtonsoft.Json;

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

[Serializable]
public record FileExtract(string Name, string Hash, long Length);

// TODO Use a dictionary to disambiguate files in different Item lists 
[Serializable]
public record FullExtract(FileExtract[] Files, (string, string)[] Props, string[] OutputFiles);

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
public record LocalInputs(LocalFileExtract[] Files, (string, string)[] Props, OutputItem[] OutputFiles)
{
    public FullExtract ToFullExtract()
    {
        return new FullExtract(Files: Files.Select(f => f.ToFileExtract()).ToArray(), Props: Props, OutputFiles: OutputFiles.Select(o => o.Name).ToArray());
    }
}

[Serializable]
public record AllCompilationMetadata(CompilationMetadata Metadata, LocalInputs LocalInputs);


public record CacheKey(string Key)
{
    public static implicit operator string(CacheKey key) => key.Key;
}

/// <summary>
/// Information about an assembly used for hash calculations in dependant projects' compilation.
/// </summary>
/// <param name="PublicRefHash">Hash of a reference assembly for this assembly, that excluded internal symbols. </param>
/// <param name="PublicAndInternalRefHash">Hash of a reference assembly for this assembly, that included internal symbols. </param>
/// <param name="InternalsVisibleTo">A list of assembly names that can access internal symbols from this assembly, via the InternalsVisibleTo attribute.</param>
public record RefData(string PublicRefHash, string PublicAndInternalRefHash, string[] InternalsVisibleTo);

public class RefTrimmer
{
    public static void MakeRefasm(string inputPath, string outputPath, LoggerBase logger, IImportFilter filter)
    {
        logger.Debug?.Invoke($"Reading assembly {inputPath}");
        var peReader = new PEReader(new FileStream(inputPath, FileMode.Open, FileAccess.Read)); 
        var metaReader = peReader.GetMetadataReader();

        if (!metaReader.IsAssembly)
            throw new Exception("File format is not supported"); 
            
        var result = MetadataImporter.MakeRefasm(metaReader, peReader, logger, filter, makeMock: false);

        logger.Debug?.Invoke($"Writing result to {outputPath}");
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        File.WriteAllBytes(outputPath, result);
    }

    public enum RefType
    {
        Public,
        PublicAndInternal
    }
    
    
    
    public static byte[] MakeRefasm(ImmutableArray<byte> content, LoggerBase logger, RefType refType)
    {
        var peReader = new PEReader(content); 
        var metaReader = peReader.GetMetadataReader();

        if (!metaReader.IsAssembly)
            throw new Exception("File format is not supported");

        IImportFilter filter = refType == RefType.PublicAndInternal
            ? new AllowPublicAndInternals()
            : new AllowPublic();
        
        return MetadataImporter.MakeRefasm(metaReader, peReader, logger, filter, makeMock: false);
    }
    
    public static string MakeRefasmAndGetHash(ImmutableArray<byte> content, LoggerBase logger, RefType refType)
    {
        var bytes = MakeRefasm(content, logger, refType);
        return Utils.ObjectToSHA256Hex(bytes);
    }

    public RefData GenerateRefData(ImmutableArray<byte> content)
    {
        var logger = new VerySimpleLogger(Console.Out, LogLevel.Warning);
        var loggerBase = new LoggerBase(logger);
        
        var publicRefHash = MakeRefasmAndGetHash(content, loggerBase, RefType.Public);
        var publicAndInternalRefHash = MakeRefasmAndGetHash(content, loggerBase, RefType.PublicAndInternal);
        
        return new RefData(
            PublicRefHash: publicRefHash,
            PublicAndInternalRefHash: publicAndInternalRefHash,
            InternalsVisibleTo: null
        );
    }
}

/// <summary>
/// A cache for storing hashes of reference assemblies (generated using Refasmer).
/// Does not store the actual assemblies, just their hashes - used for having a more robust inputs cache that does not change when private parts of references change.
/// Thread-safe.
/// </summary>
public interface IRefCache
{
    bool Exists(CacheKey key);
    RefData? Get(CacheKey key);
    void Set(CacheKey key, RefData data);
}

public interface ICache
{
    bool Exists(CacheKey key);
    void Set(CacheKey key, FullExtract fullExtract, FileInfo resultZip);
    string Get(CacheKey key);
}

public class Cache : ICache
{
    private readonly string _baseCacheDir;

    public Cache(string baseCacheDir)
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

    public static void AtomicCopy(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination)!;
        var tmpDestination = Path.Combine(dir, $".__tmp_{Guid.NewGuid()}");
        File.Copy(source, tmpDestination);
        try
        {
            File.Move(tmpDestination, destination, true);
        }
        finally
        {
            File.Delete(tmpDestination);
        }
    }

    public CacheKey[] GetAllExistingKeys()
    {
        var options = new EnumerationOptions{ReturnSpecialDirectories = false, IgnoreInaccessible = true, RecurseSubdirectories = false};
        var fullNames = Directory.EnumerateDirectories(_baseCacheDir, "*", options);
        return fullNames.Select(full => new CacheKey(Path.GetFileName(full))).ToArray();
    }

    public void Set(CacheKey key, FullExtract fullExtract, FileInfo resultZip)
    {
        var dir = new DirectoryInfo(CacheDir(key));
        if (!dir.Exists)
        {
            dir.Create();
        }

        var extractPath = ExtractPath(key);

        var outputPath = Path.Combine(dir.FullName, resultZip.Name);
        if (!File.Exists(outputPath))
        {
            AtomicCopy(resultZip.FullName, outputPath);
        }
        
        if (!File.Exists(extractPath))
        {
            var json = JsonConvert.SerializeObject(fullExtract, Formatting.Indented);
            using var tmpFile = new TempFile();
            File.WriteAllText(tmpFile.FullName, json);
            AtomicCopy(tmpFile.FullName, extractPath);
        }
    }

    public string? Get(CacheKey key)
    {
        var dir = new DirectoryInfo(CacheDir(key));
        if (dir.Exists)
        {
            var extractPath = ExtractPath(key);
            if (File.Exists(extractPath))
            {
                var outputVersionsZips = GetOutputVersions(key);
                if (outputVersionsZips.Length == 0)
                {
                    throw new Exception($"[Cache key={key}] Extract file exists, but no output files found.");
                } else if (outputVersionsZips.Length > 1)
                {
                    throw new Exception(
                        $"[Cache key={key}] Found {outputVersionsZips.Length} different outputs. Unable to pick one.");
                }
                else
                {
                    var tmpPath = Path.GetTempFileName();
                    File.Copy(outputVersionsZips[0], tmpPath, overwrite: true);
                    return tmpPath;
                }
            }
        }

        return null;
    }

    public int OutputVersionsCount(CacheKey key)
    {
        return GetOutputVersions(key).Count();
    }

    private string[] GetOutputVersions(CacheKey key)
    {
        return Directory.EnumerateFiles(CacheDir(key), "*.zip", SearchOption.TopDirectoryOnly).ToArray();
    }
}

public class TempFile : IDisposable
{
    public FileInfo File { get; }
    public string FullName => File.FullName;
    
    public TempFile()
    {
        File = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
    }
    
    public void Dispose()
    {
        File.Delete();
    }
}