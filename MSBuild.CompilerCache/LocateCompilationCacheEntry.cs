using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

public record BaseTaskInputs(
    string[] PropertyInputs,
    string[] FileInputs,
    string[] References,
    string[] OutputsToCache
);

public static class Utils
{
    public static string ObjectToSHA256Hex(object item)
    {
        using var ms = new MemoryStream();
#pragma warning disable SYSLIB0011
        var binaryFormatter = new BinaryFormatter();
        binaryFormatter.Serialize(ms, item);
        ms.Position = 0;
#pragma warning restore SYSLIB0011
        var hash = SHA256.Create().ComputeHash(ms);
        var hashString = Convert.ToHexString(hash);
        return hashString;
    }

    public static string FileToSHA256String(FileInfo fileInfo)
    {
        using var hash = SHA256.Create();
        using var f = fileInfo.OpenRead();
        var bytes = hash.ComputeHash(f);
        return Convert.ToHexString(bytes);
    }
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public abstract class BaseTask : Task
{
    [Required] public string[] PropertyInputs { get; set; }
    [Required] public string[] FileInputs { get; set; }
    [Required] public string[] References { get; set; }
    [Required] public string[] OutputsToCache { get; set; }

    protected BaseTaskInputs GatherInputs() =>
        new(
            PropertyInputs: PropertyInputs,
            FileInputs: FileInputs,
            References: References,
            OutputsToCache: OutputsToCache
        );
}

public record LocateResult(
    bool CacheHit,
    string CacheDir,
    DateTime PreCompilationTimeUtc
);

public class Locator
{
    public LocateResult Locate(BaseTaskInputs inputs, string BaseCacheDir, TaskLoggingHelper log)
    {
        var extract = CalculateExtract(inputs);
        var hashString = Utils.ObjectToSHA256Hex(extract);
        var cacheDir = Path.Combine(BaseCacheDir, hashString);

        var markerPath = Helpers.GetMarkerPath(Path.Combine(BaseCacheDir, hashString));
        bool cacheHit;
        if (!Directory.Exists(Path.Combine(BaseCacheDir, hashString)))
        {
            Directory.CreateDirectory(Path.Combine(BaseCacheDir, hashString));
            var extractPath = Path.Combine(Path.Combine(BaseCacheDir, hashString), "extract.json");
            File.WriteAllText(extractPath, JsonConvert.SerializeObject(extract, Formatting.Indented));
            log.LogMessage(MessageImportance.High, $"{Path.Combine(BaseCacheDir, hashString)} does not exist");
            cacheHit = false;
        }
        else if (!File.Exists(markerPath))
        {
            log.LogMessage(MessageImportance.High, $"Marker file {markerPath} does not exist");
            cacheHit = false;
        }
        else
        {
            log.LogMessage(MessageImportance.High, $"{Path.Combine(BaseCacheDir, hashString)} and its marker exist");
            cacheHit = true;
        }

        return new LocateResult(
            CacheHit: cacheHit,
            CacheDir: cacheDir,
            PreCompilationTimeUtc: DateTime.UtcNow
        );
    }

    public static FullExtract CalculateExtract(BaseTaskInputs baseTaskInputs)
    {
        var allFileInputs = baseTaskInputs.FileInputs.Union(baseTaskInputs.References);
        var fileExtracts = allFileInputs.OrderBy(file => file).AsParallel().Select(file =>
        {
            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists)
            {
                throw new Exception($"File input does not exist: '{file}'");
            }

            var hashString = Utils.FileToSHA256String(fileInfo);
            return new FileExtract(fileInfo.Name, hashString, fileInfo.Length);
        }).ToArray();

        var extract = new FullExtract(fileExtracts, baseTaskInputs.PropertyInputs);
        return extract;
    }
}

// ReSharper disable once UnusedType.Global
/// <summary>
/// Hashes compilation inputs and checks if a cache entry with the given hash exists. 
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class LocateCompilationCacheEntry : BaseTask
{
    private readonly Locator _locator;
#pragma warning disable CS8618
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    [Required] public string BaseCacheDir { get; set; }

    [Output] public bool CacheHit { get; private set; }
    [Output] public string CacheDir { get; private set; }
    [Output] public DateTime PreCompilationTimeUtc { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
#pragma warning restore CS8618

    public LocateCompilationCacheEntry()
    {
        _locator = new Locator();
    }
    
    public override bool Execute()
    {
        var inputs = GatherInputs();
        var results = _locator.Locate(inputs, BaseCacheDir, Log);

        CacheHit = results.CacheHit;
        CacheDir = results.CacheDir;
        PreCompilationTimeUtc = results.PreCompilationTimeUtc;
        
        return true;
    }
}