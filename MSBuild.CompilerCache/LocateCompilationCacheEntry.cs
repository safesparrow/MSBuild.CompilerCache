using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

// TODO Replace time+Length with contents hash
[Serializable]
public record FileExtract(string Name, string Hash, long Length);

// TODO add key=value
[Serializable]
public record FullExtract(FileExtract[] files, string[] props);

// ReSharper disable once UnusedType.Global
/// <summary>
/// Hashes compilation inputs and checks if a cache entry with the given hash exists. 
/// </summary>
public class LocateCompilationCacheEntry : Task
{
    [Required] public ITaskItem[] PropertyInputs { get; set; }
    [Required] public ITaskItem[] FileInputs { get; set; }
    [Required] public string BaseCacheDir { get; set; }

    [Output] public bool CacheHit { get; private set; }
    [Output] public string CacheDir { get; private set; }

    public override bool Execute()
    {
        var props = PropertyInputs.Select(p => p.ItemSpec).ToArray();
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
        var hashString = HashExtractToString(extract);
        var dir = Path.Combine(BaseCacheDir, hashString);
        CacheDir = dir;

        var markerPath = Helpers.GetMarkerPath(dir);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            var extractPath = Path.Combine(dir, "extract.json");
            File.WriteAllText(extractPath, Newtonsoft.Json.JsonConvert.SerializeObject(extract, Formatting.Indented));
            Log.LogMessage(MessageImportance.High, $"{dir} does not exist");
            CacheHit = false;
        }
        else if (!File.Exists(markerPath))
        {
            Log.LogMessage(MessageImportance.High, $"Marker file {markerPath} does not exist");
            CacheHit = false;
        }
        else
        {
            Log.LogMessage(MessageImportance.High, $"{dir} and its marker exist");
            CacheHit = true;
        }

        return true;
    }

    private static string HashExtractToString(FullExtract extract)
    {
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, extract);
        ms.Position = 0;
#pragma warning restore SYSLIB0011
        var hash = SHA256.Create().ComputeHash(ms);
        var hashString = Convert.ToHexString(hash);
        return hashString;
    }
}