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
    string IntermediateOutputPath,
    // TODO Ignored for now
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
    private void UseCacheResult(OutputItem[] cachedOutputs, string cacheZipPath)
    {
        // System.IO.Compression.ZipFile.ExtractToDirectory(cacheZipPath);
    }

    public static FileInfo BuildOutputsZip(DirectoryInfo baseDir, OutputItem[] items, AllCompilationMetadata metadata)
    {
        var outputsDir = baseDir.CreateSubdirectory("outputs");
        
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
    
    public UseOrPopulateResult UseOrPopulate(UseOrPopulateInputs inputs, TaskLoggingHelper log)
    {
        var postCompilationTimeUtc = DateTime.UtcNow;
        var dir = new DirectoryInfo(inputs.CacheDir);

        var originalHash = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputs.CacheDir));
        var recalculatedExtract = Locator.CalculateCacheExtract(inputs.Inputs);
        var recalculatedHash = Utils.ObjectToSHA256Hex(recalculatedExtract);
        var hashesMatch = originalHash == recalculatedHash;

        var outputsMap =
            inputs.Inputs.OutputsToCache
                .Select(outputPath =>
                {
                    var cachePath = Path.Combine(inputs.CacheDir, outputPath.Name);
                    return new OutputFileMap(cachePath, outputPath.LocalPath);
                }).ToArray();


        if (inputs.CacheHit && !inputs.CheckCompileOutputAgainstCache)
        {
            log.LogMessage(MessageImportance.High, $"CacheHit - copying {outputsMap.Length} files from cache");
            // TODO Parallel
            foreach (var entry in outputsMap)
            {
                log.LogMessage(MessageImportance.High, $"Copy {entry.CachePath} -> {entry.OutputPath}");
                File.Delete(entry.OutputPath);
                File.Copy(entry.CachePath, entry.OutputPath, true);
                File.SetLastWriteTimeUtc(entry.OutputPath, postCompilationTimeUtc);
                // TODO Set file timestamps - what timestamp to use? It should be compatible with the rest of the MSBuild build process.
            }
        }
        else if (inputs.CacheHit && inputs.CheckCompileOutputAgainstCache)
        {
            log.LogMessage(MessageImportance.High, $"CacheHit but '{nameof(inputs.CheckCompileOutputAgainstCache)}' enabled - verifying newly compiled outputs are same as in the cache.");
            if (!dir.Exists)
            {
                throw new Exception($"CacheHit but cache directory {dir} does not exist.");
            }
            else
            {
                // TODO Parallel
                foreach (var entry in outputsMap)
                {
                    if (!File.Exists(entry.CachePath))
                        log.LogMessage($"Despite cache hit, cache file does not exist: '{entry.CachePath}'.");
                    else
                    {
                        var outputHash = Utils.FileToSHA256String(new FileInfo(entry.OutputPath));
                        var cachedHash = Utils.FileToSHA256String(new FileInfo(entry.CachePath));
                        if (outputHash != cachedHash)
                        {
                            log.LogMessage(MessageImportance.High, $"Output hash != cache cache. Output ({entry.OutputPath}) = {outputHash}. Cache ({entry.CachePath}) = {cachedHash}");
                        }
                    }
                }

                var markerPath = Helpers.GetMarkerPath(inputs.CacheDir);
                log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
                Helpers.CreateEmptyFile(markerPath);
            }
        }
        else
        {
            log.LogMessage(MessageImportance.High, $"CacheMiss - copying {outputsMap.Length} files from output to cache");
            if (!dir.Exists) dir.Create();
            // TODO Parallel
            foreach (var entry in outputsMap)
            {
                if (File.Exists(entry.CachePath))
                    throw new Exception($"Despite cache miss, cache file exists: '{entry.CachePath}'.");
                var fileDir = Path.GetDirectoryName((string?)entry.CachePath)!;
                Directory.CreateDirectory(fileDir);
                log.LogMessage(MessageImportance.High, $"Copy {entry.OutputPath} -> {entry.CachePath}");
                File.Copy(entry.OutputPath, entry.CachePath, true);
            }

            var markerPath = Helpers.GetMarkerPath(inputs.CacheDir);
            log.LogMessage(MessageImportance.High, $"Creating marker {markerPath}");
            Helpers.CreateEmptyFile(markerPath);
        }

        return new UseOrPopulateResult();
    }

    public record OutputFileMap(string CachePath, string OutputPath);
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
    [Required] public string IntermediateOutputPath { get; set; }
#pragma warning restore CS8618
    public bool CheckCompileOutputAgainstCache { get; set; }

    public UseOrPopulateCache()
    {
        _userOrPopulator = new UserOrPopulator();
    }

    public override bool Execute()
    {
        var inputs = new UseOrPopulateInputs(
            CacheHit: CacheHit,
            CacheDir: CacheDir,
            IntermediateOutputPath: IntermediateOutputPath,
            Inputs: GatherInputs(),
            CheckCompileOutputAgainstCache: CheckCompileOutputAgainstCache
        );
        var results = _userOrPopulator.UseOrPopulate(inputs, Log);
        return true;
    }
}