using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Task = Microsoft.Build.Utilities.Task;

namespace MSBuild.CompilerCache;

public record UseOrPopulateResult;

public record UseOrPopulateInputs(LocateResult LocateResult);

public class Populator
{
    private readonly ICache _cache;
    private readonly IRefCache _refCache;

    public Populator(ICache cache, IRefCache refCache)
    {
        _cache = cache;
        _refCache = refCache;
    }

    public static FileInfo BuildOutputsZip(DirectoryInfo baseTmpDir, OutputItem[] items,
        AllCompilationMetadata metadata,
        TaskLoggingHelper? log = null)
    {
        var outputsDir = baseTmpDir.CreateSubdirectory("outputs_zip_building");

        var outputExtracts =
            items.Select(item =>
            {
                var tempPath = outputsDir.CombineAsFile(item.CacheFileName);
                log?.LogMessage(MessageImportance.Normal, $"CompilationCache: Copy {item.LocalPath} -> {tempPath.FullName}");
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

        var tempZipPath = baseTmpDir.CombineAsFile($"{hashForFileName}.zip");
        ZipFile.CreateFromDirectory(outputsDir.FullName, tempZipPath.FullName,
            CompressionLevel.NoCompression, includeBaseDirectory: false);
        return tempZipPath;
    }

    public UseOrPopulateResult UseOrPopulate(UseOrPopulateInputs inputs, TaskLoggingHelper log,
        RefTrimmingConfig trimmingConfig)
    {
        var postCompilationTimeUtc = DateTime.UtcNow;
        var decomposed = inputs.LocateResult.DecomposedCompilerProps;
        var outputs = decomposed.OutputsToCache;
        var assemblyName = inputs.LocateResult.Inputs.AssemblyName;
        var localInputs = Locator.CalculateLocalInputs(decomposed, _refCache, assemblyName, trimmingConfig);
        var extract = localInputs.ToFullExtract();
        var localInputsHash = Utils.ObjectToSHA256Hex(localInputs);
        var cacheKey = inputs.LocateResult.CacheKey;
        var hashesMatch = inputs.LocateResult.LocalInputsHash == localInputsHash;
        log.LogMessage(MessageImportance.Normal,
            $"CompilationCache info: Match={hashesMatch} LocatorKey={inputs.LocateResult.LocalInputsHash} RecalculatedKey={localInputsHash}");

        if (hashesMatch)
        {
            log.LogMessage(MessageImportance.Normal,
                $"CompilationCache miss - copying {outputs.Length} files from output to cache");
            var meta = Locator.GetCompilationMetadata(postCompilationTimeUtc);
            var stuff = new AllCompilationMetadata(Metadata: meta, LocalInputs: localInputs);
            using var tmpDir = new DisposableDir();
            var outputZip = BuildOutputsZip(tmpDir, outputs, stuff, log);
            _cache.Set(cacheKey, extract, outputZip);
        }
        else
        {
            log.LogMessage(MessageImportance.Normal,
                $"CompilationCache miss and inputs changed during compilation. The cache will not be populated as we are not certain what inputs the compiler used.");
        }

        return new UseOrPopulateResult();
    }
}