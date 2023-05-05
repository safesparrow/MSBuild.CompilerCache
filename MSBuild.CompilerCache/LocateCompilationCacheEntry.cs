using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

public record BaseTaskInputs(
    string ProjectFullPath,
    string PropertyInputs,
    string[] FileInputs,
    string[] References,
    OutputItem[] OutputsToCache
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
    [Required] public string PropertyInputs { get; set; }
    [Required] public string[] FileInputs { get; set; }
    [Required] public string[] References { get; set; }
    [Required] public ITaskItem[] OutputsToCache { get; set; }
    [Required] public string ProjectFullPath { get; set; }

    protected BaseTaskInputs GatherInputs()
    {
        
        return new BaseTaskInputs(
            ProjectFullPath: ProjectFullPath,
            PropertyInputs: PropertyInputs,
            FileInputs: FileInputs.OrderBy(x => x).ToArray(),
            References: References.OrderBy(x => x).ToArray(),
            OutputsToCache: OutputsToCache.Select(ParseOutputToCache).ToArray()
        );
    }

    private static OutputItem ParseOutputToCache(ITaskItem arg) =>
        new(
            Name: arg.GetMetadata("Name") ?? throw new ArgumentException($"The '{arg.ItemSpec}' item has no 'Name' metadata."),
            LocalPath: arg.ItemSpec
        );
}

public record LocateResult(
    bool CacheHit,
    CacheKey CacheKey,
    string LocalInputsHash,
    DateTime PreCompilationTimeUtc
);

public class Locator
{
    public LocateResult Locate(BaseTaskInputs inputs, string baseCacheDir, TaskLoggingHelper log)
    {
        var cache = new Cache(baseCacheDir);
        var localInputs = CalculateLocalInputs(inputs);
        var extract = localInputs.ToFullExtract();
        var hashString = Utils.ObjectToSHA256Hex(extract);
        var cacheKey = UserOrPopulator.GenerateKey(inputs, hashString);
        
        
        
        var cacheDir = Path.Combine(baseCacheDir, hashString);

        var markerPath = Helpers.GetMarkerPath(Path.Combine(baseCacheDir, hashString));
        var cacheHit = cache.Exists(cacheKey);
        if (!cacheHit)
        {
            log.LogMessage(MessageImportance.High, $"Locate for {cacheKey} was a miss.");
        }
        else
        {
            log.LogMessage(MessageImportance.High, $"Locate for {cacheKey} was a hit.");
            cacheHit = true;
        }

        File.WriteAllText(DateTime.UtcNow.Ticks.ToString() + ".json", JsonConvert.SerializeObject(localInputs, Formatting.Indented));
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);

        return new LocateResult(
            CacheHit: cacheHit,
            CacheKey: cacheKey,
            LocalInputsHash: localInputsHash,
            PreCompilationTimeUtc: DateTime.UtcNow
        );
    }

    public static FullExtract CalculateCacheExtract(BaseTaskInputs baseTaskInputs)
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

        var outputNames = baseTaskInputs.OutputsToCache.Select(o => o.Name).ToArray();
        var extract = new FullExtract(fileExtracts, baseTaskInputs.PropertyInputs, outputNames);
        return extract;
    }
    
    public static LocalInputs CalculateLocalInputs(BaseTaskInputs inputs)
    {
        var allFileInputs = inputs.FileInputs.Union(inputs.References);
        var fileExtracts = allFileInputs.OrderBy(file => file).AsParallel().AsOrdered().Select(GetLocalFileExtract).ToArray();

        return new LocalInputs(fileExtracts, inputs.PropertyInputs, inputs.OutputsToCache);
    }

    public static LocalFileExtract GetLocalFileExtract(string filepath)
    {
        var fileInfo = new FileInfo(filepath);
        if (!fileInfo.Exists)
        {
            throw new Exception($"File does not exist: '{filepath}'");
        }

        var hashString = Utils.FileToSHA256String(fileInfo);
        return new LocalFileExtract(fileInfo.FullName, hashString, fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    public static PreCompilationMetadata GetPreCompilationMetadata() =>
        new(
            Hostname: Environment.MachineName,
            Username: Environment.UserName,
            StartTimeUtc: DateTime.UtcNow,
            WorkingDirectory: Environment.CurrentDirectory
        );
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
    [Output] public string CacheKey { get; private set; }
    [Output] public string LocalInputsHash { get; set; }
    [Output] public DateTime PreCompilationTimeUtc { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
#pragma warning restore CS8618

    public LocateCompilationCacheEntry()
    {
        _locator = new Locator();
    }
    
    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, $"PropertyInputs={PropertyInputs}");
        var inputs = GatherInputs();
        var results = _locator.Locate(inputs, BaseCacheDir, Log);

        CacheHit = results.CacheHit;
        CacheKey = results.CacheKey;
        LocalInputsHash = results.LocalInputsHash;
        PreCompilationTimeUtc = results.PreCompilationTimeUtc;
        
        return true;
    }

}