using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;

namespace MSBuild.CompilerCache;

using Microsoft.Build.Framework;

public record UseOrPopulateInputs(
    BaseTaskInputs Inputs,
    bool CacheHit,
    string CacheDir,
    bool CheckCompileOutputAgainstCache
);

public record UseOrPopulateResult(
);

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
    
    public static FileInfo BuildOutputsZip(DirectoryInfo baseDir, OutputItem[] items, AllCompilationMetadata metadata)
    {
        var outputsDir = baseDir.CreateSubdirectory("outputs_zip_building");
        
        var outputExtracts =
            items.Select(item =>
            {
                var tempPath = outputsDir.CombineAsFile(item.Name);
                File.Copy(item.LocalPath, tempPath.FullName);
                return Locator.GetLocalFileExtract(tempPath.FullName).ToFileExtract();
            }).ToArray();

        var hashForFileName = Utils.ObjectToSHA256Hex(outputExtracts);

        var outputsExtractJson = Newtonsoft.Json.JsonConvert.SerializeObject(outputExtracts, Formatting.Indented);
        var outputsExtractJsonPath = outputsDir.CombineAsFile("__outputs.json");
        File.WriteAllText(outputsExtractJsonPath.FullName, outputsExtractJson);
        
        var metaJson = Newtonsoft.Json.JsonConvert.SerializeObject(metadata, Formatting.Indented);
        var metaPath = outputsDir.CombineAsFile("__inputs.json");
        File.WriteAllText(metaPath.FullName, metaJson);

        var tempZipPath = baseDir.CombineAsFile($"{hashForFileName}.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(outputsDir.FullName, tempZipPath.FullName,
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
                    items.SingleOrDefault(it => it.Name == entry.Name)
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

    public CacheKey GenerateKey(BaseTaskInputs inputs, string hash)
    {
        var dateString = $"{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}";
        var name = inputs.GlobalProps.ProjectNameWithExtension;
        return new CacheKey($"{name}_{dateString}_{hash}");
    }
    
    public UseOrPopulateResult UseOrPopulate(UseOrPopulateInputs inputs, TaskLoggingHelper log)
    {
        using var tmpDir = new DisposableDir();
        var postCompilationTimeUtc = DateTime.UtcNow;
        var dir = new DirectoryInfo(inputs.CacheDir);

        var originalHash = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputs.CacheDir));
        var recalculatedExtract = Locator.CalculateCacheExtract(inputs.Inputs);
        var recalculatedHash = Utils.ObjectToSHA256Hex(recalculatedExtract);
        var cacheKey = GenerateKey(inputs.Inputs, recalculatedHash);
        var hashesMatch = originalHash == cacheKey.Key;
        // TODO check hashes

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
                var localInputs = Locator.CalculateLocalInputs(inputs.Inputs);
                var pre = Locator.GetPreCompilationMetadata();
                var stuff = new AllCompilationMetadata(Metadata: pre, LocalInputs: localInputs);
                var outputZip = BuildOutputsZip(dir, outputs, stuff);
                _cache.Set(cacheKey, recalculatedExtract, outputZip);
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
    private readonly UserOrPopulator _userOrPopulator;
#pragma warning disable CS8618
    [Required] public bool CacheHit { get; set; }
    [Required] public string CacheDir { get; set; }
    // TODO Unused - remove.
    [Required] public string IntermediateOutputPath { get; set; }
#pragma warning restore CS8618
    public bool CheckCompileOutputAgainstCache { get; set; }

    public UseOrPopulateCache()
    {
        _userOrPopulator = new UserOrPopulator(new Cache(Path.GetDirectoryName(CacheDir)!));
    }

    public override bool Execute()
    {
        var inputs = new UseOrPopulateInputs(
            CacheHit: CacheHit,
            CacheDir: CacheDir,
            Inputs: GatherInputs(),
            CheckCompileOutputAgainstCache: CheckCompileOutputAgainstCache
        );
        var results = _userOrPopulator.UseOrPopulate(inputs, Log);
        return true;
    }
}