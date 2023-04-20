using System.Security.Cryptography;
using JetBrains.Refasmer;

namespace MSBuild.CompilerCache;

public record RefCacheHash(string Hash);

public class RefCache
{
    public DirectoryInfo BaseDir { get; }

    public RefCache(DirectoryInfo baseDir)
    {
        BaseDir = baseDir;
        baseDir.Create();
    }
    
    public string EntryPath(RefCacheHash hash) =>
        Path.Combine(BaseDir.FullName, $"{hash.Hash}.txt");

    public string? Get(RefCacheHash hash)
    {
        var path = EntryPath(hash);
        if (File.Exists(path))
        {
            return path;
        }

        return null;
    }

    public void Set(RefCacheHash hash, string filepath)
    {
        var cachePath = EntryPath(hash);
        if (File.Exists(cachePath))
        {
            throw new Exception($"RefCache entry already exists: {cachePath}");
        }

        File.Copy(filepath, cachePath, overwrite: false);
    }
}

public class RefTrimming
{
    public RefCache Cache { get; }

    public RefTrimming(RefCache cache)
    {
        Cache = cache;
    }
    
    public FileExtract[] TrimReferences(string[] paths)
    {
        return paths.Select(TrimReferenceOrGetFromCache).ToArray();
    }

    public FileExtract TrimReferenceOrGetFromCache(string originalDll)
    {
        var originalDllFile = new FileInfo(originalDll);
        var originalHash = new RefCacheHash(FileToSHA1String(originalDllFile));
        var cachedPath = Cache.Get(originalHash);
        if (cachedPath == null)
        {
            var tmpRefFile = Path.GetTempFileName();
            // TODO Construct a single shared metadataImporter
            File.Delete(tmpRefFile);
            MetadataImporter.MakeRefasm(originalDll, tmpRefFile, new LoggerBase(new Logger()));
            Cache.Set(originalHash, tmpRefFile);
            var extract = GenerateFileExtract(tmpRefFile) with { Name = originalDllFile.Name, Trimmed = true };
            File.Delete(tmpRefFile);
            return extract;
        }
        else
        {
            var extract = GenerateFileExtract(cachedPath) with { Name = originalDllFile.Name, Trimmed = true };
            return extract;
        }
    }

    public FileExtract GenerateFileExtract(string path)
    {
        var fileInfo = new FileInfo(path);
        var hashString = FileToSHA1String(fileInfo);
        return new FileExtract(fileInfo.Name, hashString, fileInfo.Length);
    }

    public static string FileToSHA1String(FileInfo fileInfo)
    {
        using var hash = SHA1.Create();
        using var f = fileInfo.OpenRead();
        var bytes = hash.ComputeHash(f);
        return Convert.ToHexString(bytes);
    }

    public class Logger : ILogger
    {
        public void Log(LogLevel logLevel, string message)
        {
            if (IsEnabled(logLevel))
            {
                Console.WriteLine($"[Refasmer {logLevel}] {message}");
            }
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    }
}