using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

// TODO Replace time+Length with contents hash
[Serializable]
public record FileExtract(string Name, string Hash, long Length);

// TODO add key=value
[Serializable]
public record FullExtract(FileExtract[] files, string[] props);

// <UseOrPopulateCache OutputsToCache="@(CompileOutputsToCache)" CacheHit="$(CacheHit)" CacheDir="$(CacheDir)" />
public static class Helpers
{
    public static string GetMarkerPath(string cacheDir)
    {
        var x = 3;
        Console.WriteLine($"x = {x}");
        var y = 12352;
        Console.WriteLine($"y = {y}");
        return Path.Combine(cacheDir, "marker.data");
    }
}

public class UseOrPopulateCache : Task
{
    [Required]
    public ITaskItem[] OutputsToCache { get; set; }
    [Required]
    public bool CacheHit { get; set; }
    [Required]
    public string CacheDir { get; set; }
    [Required]
    public string IntermediateOutputPath { get; set; }

    public record OutputFileMap(string CachePath, string OutputPath);
    
    public override bool Execute()
    {
        var dir = new DirectoryInfo(CacheDir);

        var map =
            OutputsToCache
                .Select(o =>
                {
                    var outputPath = o.ItemSpec!;
                    var relativePath = Path.GetRelativePath(IntermediateOutputPath, outputPath);
                    var cachePath = Path.Combine(CacheDir, relativePath);
                    return new OutputFileMap(cachePath, outputPath);
                }).ToArray();

        if (CacheHit)
        {
            Log.LogMessage(MessageImportance.High, $"CacheHit - copying {map.Length} files from cache");
            // TODO Parallel
            foreach (var entry in map)
            {
                Log.LogMessage(MessageImportance.High, $"Copy {entry.CachePath} -> {entry.OutputPath}");
                File.Delete(entry.OutputPath);
                File.Copy(entry.CachePath, entry.OutputPath, true);
                // TODO Set timestamp
            }
        }
        else
        {
            Log.LogMessage(MessageImportance.High, $"CacheMiss - copying {map.Length} files from output to cache");
            if(!dir.Exists) dir.Create();
            // TODO Parallel
            foreach (var entry in map)
            {
                if (File.Exists(entry.CachePath))
                    throw new Exception($"Despite cache miss, cache file exists: '{entry.CachePath}'.");
                var fileDir = Path.GetDirectoryName(entry.CachePath)!;
                Directory.CreateDirectory(fileDir);
                Log.LogMessage(MessageImportance.High, $"Copy {entry.OutputPath} -> {entry.CachePath}");
                File.Copy(entry.OutputPath, entry.CachePath, true);
            }

            var markerPath = Helpers.GetMarkerPath(CacheDir);
            Log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
            // ReSharper disable once EmptyEmbeddedStatement
            using (File.Create(markerPath));
        }

        return true;
    }
}

// ReSharper disable once UnusedType.Global
public class CacheTask : Task
{
    [Required]
    public ITaskItem[] PropertyInputs { get; set; }
    [Required]
    public ITaskItem[] FileInputs { get; set; }
    [Required]
    public string BaseCacheDir { get; set; }
    
    [Output]
    public bool CacheHit { get; private set; }
    [Output]
    public string CacheDir { get; private set; }


    public override bool Execute()
    {
        var props = PropertyInputs.Select(p => p.ItemSpec).ToArray();
        var baseDir = @"c:\projekty\zip\zip\zip\";
        var fileExtracts = FileInputs.AsParallel().Select(file =>
        {
            var filepath = file.ItemSpec;
            var fileInfo = new FileInfo(filepath);
            if (!fileInfo.Exists)
            {
                throw new Exception($"File input does not exist: '{filepath}'");
            }

            using var hash = SHA1.Create();
            using var f = fileInfo.OpenRead();
            var bytes = hash.ComputeHash(f);
            var hashString = Convert.ToHexString(bytes);
            return new FileExtract(fileInfo.Name, hashString, fileInfo.Length);
        }).ToArray();

        var extract = new FullExtract(fileExtracts, props);

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(extract, Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText("extract.json", json);
        
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, extract);
        ms.Position = 0;
#pragma warning restore SYSLIB0011
        var hash = System.Security.Cryptography.SHA256.Create().ComputeHash(ms);
        var hashString = Convert.ToHexString(hash);
        var dir = Path.Combine(BaseCacheDir, hashString);
        CacheDir = dir;

        var markerPath = Helpers.GetMarkerPath(dir);
        if (!Directory.Exists(dir))
        {
            Log.LogMessage(MessageImportance.High, $"{dir} does not exist");
            CacheHit = false;
            Directory.CreateDirectory(dir);
        }
        else if(!File.Exists(markerPath))
        {
            Log.LogMessage(MessageImportance.High, $"Marker file {markerPath} does not exist");
            CacheHit = false;
        }
        else
        {
            CacheHit = true;
            Log.LogMessage(MessageImportance.High, $"{dir} and its marker exist");
        }
        
        return true;
    }
}