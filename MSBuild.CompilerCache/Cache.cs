using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using Newtonsoft.Json;


namespace MSBuild.CompilerCache;

[Serializable]
public record FileExtract(string Name, string? Hash, long Length);

// TODO Use a dictionary to disambiguate files in different Item lists 
[Serializable]
public record FullExtract(FileExtract[] Files, (string, string)[] Props, string[] OutputFiles);

[Serializable]
public record LocalFileExtract
{
    public LocalFileExtract(FileCacheKey Info, string? Hash)
    {
        this.Info = Info;
        this.Hash = Hash;
        if(Info.FullName == null) throw new Exception("File info name empty");
    }

    public FileCacheKey Info { get; set; }
    public string Path => Info.FullName;
    public long Length => Info.Length;
    public DateTime LastWriteTimeUtc => Info.LastWriteTimeUtc;
    public string? Hash { get; set; }
    public FileExtract ToFileExtract() => new(Name: System.IO.Path.GetFileName(Path), Hash: Hash, Length: Length);

    public void Deconstruct(out FileCacheKey Info, out string? Hash)
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
public record LocalInputs(LocalFileExtract[] Files, (string, string)[] Props, OutputItem[] OutputFiles)
{
    public FullExtract ToFullExtract()
    {
        return new FullExtract(Files: Files.Select(f => f.ToFileExtract()).ToArray(), Props: Props,
            OutputFiles: OutputFiles.Select(o => o.Name).ToArray());
    }
}

[Serializable]
public record AllCompilationMetadata(CompilationMetadata Metadata, LocalInputs LocalInputs);

public record CacheKey(string Key)
{
    public static implicit operator string(CacheKey key) => key.Key;
}

public interface ICache
{
    bool Exists(CacheKey key);
    void Set(CacheKey key, FullExtract fullExtract, FileInfo resultZip);
    string? Get(CacheKey key);
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

    public static bool AtomicCopy(string source, string destination, bool throwIfDestinationExists = true)
    {
        var dir = Path.GetDirectoryName(destination)!;
        var tmpDestination = Path.Combine(dir, $".__tmp_{Guid.NewGuid()}");
        File.Copy(source, tmpDestination);
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
            AtomicCopy(resultZip.FullName, outputPath, throwIfDestinationExists: false);
        }

        var jsonOptions = new JsonSerializerOptions()
        {
            WriteIndented = true
        };
        
        if (!File.Exists(extractPath))
        {
            using var tmpFile = new TempFile();
            {
                using var fs = tmpFile.File.OpenWrite();
                System.Text.Json.JsonSerializer.Serialize(fs, fullExtract, jsonOptions);
            }
            AtomicCopy(tmpFile.FullName, extractPath, throwIfDestinationExists: false);
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
                }
                else if (outputVersionsZips.Length > 1)
                {
                    throw new Exception(
                        $"[Cache key={key}] Found {outputVersionsZips.Length} different outputs. Unable to pick one.");
                }
                else
                {
                    var tmpPath = Path.GetTempFileName();
                    ActionWithRetries(() => File.Copy(outputVersionsZips[0], tmpPath, overwrite: true));
                    return tmpPath;
                }
            }
        }

        return null;
    }
    
    private static void ActionWithRetries(Action action)
    {
        var attempts = 5;
        var retryDelay = 50;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                action();
                return;
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

    public int OutputVersionsCount(CacheKey key)
    {
        return GetOutputVersions(key).Count();
    }

    private string[] GetOutputVersions(CacheKey key)
    {
        return Directory.EnumerateFiles(CacheDir(key), "*.zip", SearchOption.TopDirectoryOnly).ToArray();
    }
}