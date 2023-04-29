using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

[Serializable]
public record FileExtract(string Name, string Hash, long Length);

// TODO add key=value
[Serializable]
public record FullExtract(FileExtract[] files, string[] props);

/// <summary>
/// Information about the environment that is not part of the compilation inputs,
/// but could potentially cause difference in results,
/// and can be recorded for investigation.
/// </summary>
public record PreCompilationMetadata(string Hostname, string Username, DateTime StartTimeUtc);

public record PostCompilationMetadata(string Hostname, string Username, DateTime StartTimeUtc, DateTime StopTimeUtc);

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public abstract class BaseTask : Task
{
    [Required] public string[] PropertyInputs { get; set; }
    [Required] public string[] FileInputs { get; set; }
    [Required] public string[] References { get; set; }
    [Required] public string[] OutputsToCache { get; set; }

    protected FullExtract CalculateExtract()
    {
        var fileExtracts = FileInputs.Union(References).AsParallel().OrderBy(file => file).Select(file =>
        {
            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists)
            {
                throw new Exception($"File input does not exist: '{file}'");
            }

            var hashString = LocateCompilationCacheEntry.FileToSHA1String(fileInfo);
            return new FileExtract(fileInfo.Name, hashString, fileInfo.Length);
        }).ToArray();

        var extract = new FullExtract(fileExtracts, PropertyInputs);
        return extract;
    }

    protected static string HashExtractToString(FullExtract extract)
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

// ReSharper disable once UnusedType.Global
/// <summary>
/// Hashes compilation inputs and checks if a cache entry with the given hash exists. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class LocateCompilationCacheEntry : BaseTask
{
#pragma warning disable CS8618
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    [Required] public string BaseCacheDir { get; set; }

    [Output] public bool CacheHit { get; private set; }
    [Output] public string CacheDir { get; private set; }
    [Output] public DateTime PreCompilationTimeUtc { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
#pragma warning restore CS8618

    public override bool Execute()
    {
        ExecuteInner();
        return true;
    }

    internal void ExecuteInner()
    {
        var extract = CalculateExtract();
        var hashString = HashExtractToString(extract);
        var dir = Path.Combine(BaseCacheDir, hashString);
        CacheDir = dir;

        var markerPath = Helpers.GetMarkerPath(dir);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            var extractPath = Path.Combine(dir, "extract.json");
            File.WriteAllText(extractPath, JsonConvert.SerializeObject(extract, Formatting.Indented));
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

        PreCompilationTimeUtc = DateTime.UtcNow;
    }

    public static string FileToSHA1String(FileInfo fileInfo)
    {
        using var hash = SHA1.Create();
        using var f = fileInfo.OpenRead();
        var bytes = hash.ComputeHash(f);
        return Convert.ToHexString(bytes);
    }
}