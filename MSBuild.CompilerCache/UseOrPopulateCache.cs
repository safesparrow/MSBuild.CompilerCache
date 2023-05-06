using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

public record UseOrPopulateInputs(
    BaseTaskInputs Inputs,
    bool CacheHit,
    CacheKey CacheKey,
    string LocatorLocalInputsHash,
    bool CheckCompileOutputAgainstCache
);

public record UseOrPopulateResult;

public static class DirectoryInfoExtensions
{
    public static string Combine(this DirectoryInfo dir, string suffix) => Path.Combine(dir.FullName, suffix);

    public static FileInfo CombineAsFile(this DirectoryInfo dir, string suffix) =>
        new FileInfo(Path.Combine(dir.FullName, suffix));
    
    public static DirectoryInfo CombineAsDir(this DirectoryInfo dir, string suffix) =>
        new DirectoryInfo(Path.Combine(dir.FullName, suffix));
}

public class UserOrPopulator
{
    private readonly ICache _cache;
    
    public UserOrPopulator(ICache cache)
    {
        _cache = cache;
    }
    
    public static FileInfo BuildOutputsZip(DirectoryInfo baseDir, OutputItem[] items, AllCompilationMetadata metadata, TaskLoggingHelper? log = null)
    {
        var outputsDir = baseDir.CreateSubdirectory("outputs_zip_building");
        
        var outputExtracts =
            items.Select(item =>
            {
                var tempPath = outputsDir.CombineAsFile(item.CacheFileName);
                log?.LogMessage(MessageImportance.High, $"Copy {item.LocalPath} -> {tempPath.FullName}");
                File.Copy(item.LocalPath, tempPath.FullName);
                return Locator.GetLocalFileExtract(tempPath.FullName).ToFileExtract();
            }).ToArray();

        var hashForFileName = Utils.ObjectToSHA256Hex(outputExtracts);

        var outputsExtractJson = JsonConvert.SerializeObject(outputExtracts, Formatting.Indented);
        var outputsExtractJsonPath = outputsDir.CombineAsFile("__outputs.json");
        File.WriteAllText(outputsExtractJsonPath.FullName, outputsExtractJson);
        
        var metaJson = JsonConvert.SerializeObject(metadata, Formatting.Indented);
        var metaPath = outputsDir.CombineAsFile("__inputs.json");
        File.WriteAllText(metaPath.FullName, metaJson);

        var tempZipPath = baseDir.CombineAsFile($"{hashForFileName}.zip");
        ZipFile.CreateFromDirectory(outputsDir.FullName, tempZipPath.FullName,
            CompressionLevel.NoCompression, includeBaseDirectory: false);
        return tempZipPath;
    }
    
    public static void UseCachedOutputs(string outputsZipPath, OutputItem[] items, DateTime postCompilationTimeUtc)
    {
        var tempPath = Path.GetTempFileName();
        File.Copy(outputsZipPath, tempPath, overwrite: true);
        try
        {
            using var a = ZipFile.OpenRead(tempPath);
            foreach (var entry in a.Entries.Where(e => !e.Name.StartsWith("__")))
            {
                var outputItem =
                    items.SingleOrDefault(it => it.CacheFileName == entry.Name)
                    ?? throw new Exception($"Cached outputs contain an unknown file '{entry.Name}'.");
                entry.ExtractToFile(outputItem.LocalPath, overwrite: true);
                File.SetLastWriteTimeUtc(outputItem.LocalPath, postCompilationTimeUtc);
            }
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    public static CacheKey GenerateKey(BaseTaskInputs inputs, string hash)
    {
        //var dateString = $"{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}";
        var name = Path.GetFileName(inputs.ProjectFullPath);
        return new CacheKey($"{name}_{hash}");
    }
    
    public UseOrPopulateResult UseOrPopulate(UseOrPopulateInputs inputs, TaskLoggingHelper log)
    {
        var postCompilationTimeUtc = DateTime.UtcNow;
        
        var localInputs = Locator.CalculateLocalInputs(inputs.Inputs);
        var extract = localInputs.ToFullExtract();
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);
        var cacheKey = inputs.CacheKey;
        var hashesMatch = inputs.LocatorLocalInputsHash == localInputsHash;
        log.LogMessage(MessageImportance.High, $"Match={hashesMatch} LocatorKey={inputs.LocatorLocalInputsHash} RecalculatedKey={localInputsHash}");

        var compilationHappened =
            !inputs.CacheHit || inputs.CheckCompileOutputAgainstCache;

        var outputs = inputs.Inputs.OutputsToCache;
        if (!compilationHappened)
        {
            if (hashesMatch)
            {
                log.LogMessage(MessageImportance.High, $"CacheHit - copying {outputs.Length} files from cache");

                var cachedOutputTmpZip = _cache.Get(cacheKey);
                try
                {
                    UseCachedOutputs(cachedOutputTmpZip, outputs, postCompilationTimeUtc);
                }
                finally
                {
                    File.Delete(cachedOutputTmpZip);
                }
            }
            else
            {
                throw new InvalidOperationException($"Initial inputs generated a CacheHit, but inputs changed since then. Since compilation was already skipped, we are unable to proceed.");
            }
        }
        else // Compilation happened
        {
            if (hashesMatch)
            {
                log.LogMessage(MessageImportance.High,
                    $"CacheMiss - copying {outputs.Length} files from output to cache");
                var pre = Locator.GetPreCompilationMetadata();
                var stuff = new AllCompilationMetadata(Metadata: pre, LocalInputs: localInputs);
                using var tmpDir = new DisposableDir();
                var outputZip = BuildOutputsZip(tmpDir, outputs, stuff, log);
                _cache.Set(cacheKey, extract, outputZip);
            }
            else
            {
                log.LogWarning($"CacheMiss and inputs changed during compilation. The cache will not be populated as we are not certain what inputs the compiler used.");
            }
        }

        return new UseOrPopulateResult();
    }
}

// ReSharper disable once UnusedType.Global
/// <summary>
/// Either use results from an existing cache entry, or populate it with newly compiled outputs.
/// Example usage: <UseOrPopulateCache OutputsToCache="@(CompileOutputsToCache)" CacheHit="$(CacheHit)" CacheDir="$(CacheDir)" IntermediateOutputPath="..." />
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class UseOrPopulateCache : BaseTask
{
#pragma warning disable CS8618
    [Required] public bool CacheHit { get; set; }
    [Required] public string CacheKey { get; set; }
    [Required] public string LocalInputsHash { get; set; }
    // TODO Unused - remove.
    [Required] public string IntermediateOutputPath { get; set; }
    [Required] public bool CheckCompileOutputAgainstCache { get; set; }
#pragma warning restore CS8618

    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, $"PropertyInputs={string.Join(",", PropertyInputs)}");
        var _userOrPopulator = new UserOrPopulator(new Cache(BaseCacheDir));
        var inputs = new UseOrPopulateInputs(
            CacheHit: CacheHit,
            CacheKey: new CacheKey(CacheKey),
            Inputs: GatherInputs(),
            LocatorLocalInputsHash: LocalInputsHash,
            CheckCompileOutputAgainstCache: CheckCompileOutputAgainstCache
        );
        var results = _userOrPopulator.UseOrPopulate(inputs, Log);
        return true;
    }
}